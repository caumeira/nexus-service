using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Binary-file-backed IMetricsHistoryStore, replacing SqliteMetricsHistoryStore
/// one phase at a time (see the metrics-store design notes). Phase 1 wired the
/// 1Hz scalar series (cpu/mem/net-in/net-out/cpu-temp) through ScalarRingStore
/// and SuperBlock; Phase 2 adds the scalar minute rollup
/// (ScalarMinuteRollupRing) and per-entity gpu/fan history (GpuRingStore/
/// FanRingStore, each their own raw-second plus minute-rollup rings). Storage
/// or RAM component temperature and the 90-day temperature bucket rollup are
/// still SQLite-only: Append silently drops those readings from an incoming
/// MetricSample, Query always returns ComponentTemps as an empty list, and
/// the two matching decimated queries throw NotImplementedException noting
/// the phase that adds them.
///
/// Unlike SqliteMetricsHistoryStore, there is no internal write lock: every
/// ring's own crash-safety protocol (see RingFile) is what makes reads
/// lock-free against a concurrent writer, on the same single-writer
/// assumption the whole store is built on (MetricsSampler is the only
/// Append caller).
/// </summary>
public sealed class BinaryMetricsHistoryStore : IMetricsHistoryStore
{
    private const string SuperBlockFileName = "super";
    private const string ScalarsFileName = "scalars.ring";
    private const string ScalarsMinuteFileName = "scalars.min";

    // Real hardware never comes close to this many distinct GPUs or fan
    // channels; the cap only exists so the store's footprint stays bounded
    // if it somehow did - see EntityRegistry.RegisterOrGet.
    private const int EntityCapacity = 32;

    private readonly SuperBlock _superBlock;
    private readonly ScalarRingStore _scalars;
    private readonly ScalarMinuteRollupRing _scalarRollup;
    private readonly GpuRingStore _gpus;
    private readonly FanRingStore _fans;

    public BinaryMetricsHistoryStore(string dbDir)
    {
        Directory.CreateDirectory(dbDir);
        var secondCapacity = MetricsHistory.RetentionDays * 86_400L;
        var minuteCapacity = MetricsHistory.RetentionDays * 1440L;

        _superBlock = SuperBlock.CreateOrOpen(Path.Combine(dbDir, SuperBlockFileName), secondCapacity);
        _scalars = new ScalarRingStore(Path.Combine(dbDir, ScalarsFileName), secondCapacity, _superBlock.PruneFloorSec);
        _scalarRollup = new ScalarMinuteRollupRing(Path.Combine(dbDir, ScalarsMinuteFileName), minuteCapacity, _superBlock.PruneFloorSec);
        _gpus = new GpuRingStore(Path.Combine(dbDir, "gpu"), secondCapacity, minuteCapacity, EntityCapacity, _superBlock.PruneFloorSec);
        _fans = new FanRingStore(Path.Combine(dbDir, "fan"), secondCapacity, minuteCapacity, EntityCapacity, _superBlock.PruneFloorSec);
    }

    public void Append(IReadOnlyList<MetricSample> samples, long? pruneCutoffSec)
    {
        if (samples.Count == 0 && pruneCutoffSec is null)
        {
            return;
        }

        if (samples.Count > 0)
        {
            _scalars.Append(samples);
            _gpus.Append(samples);
            _fans.Append(samples);

            var touchedMinutes = new HashSet<long>();
            foreach (var s in samples)
            {
                touchedMinutes.Add(MinuteTier.FloorToMinuteSec(s.TsSec));
            }
            foreach (var minute in touchedMinutes)
            {
                RebuildScalarMinute(minute);
            }
            _scalarRollup.Flush();
        }

        if (pruneCutoffSec is { } cutoff)
        {
            var changed = _scalars.RaisePruneFloor(cutoff);
            changed |= _scalarRollup.RaisePruneFloor(cutoff);
            changed |= _gpus.RaisePruneFloor(cutoff);
            changed |= _fans.RaisePruneFloor(cutoff);
            if (changed)
            {
                _superBlock.Persist(cutoff, _superBlock.SourceFloorSec);
            }
        }
    }

    // Rebuilds (never accumulates) a touched minute from ScalarRingStore's
    // raw per-second data, which by this point in Append already reflects
    // everything this batch just wrote - see ScalarMinuteRollupRing's class
    // doc for the replay-dedup and floor-filtering implications.
    private void RebuildScalarMinute(long minuteFloorSec)
    {
        var raw = _scalars.Query(minuteFloorSec, minuteFloorSec + 59);
        var agg = new ScalarMinuteAgg(
            FieldAgg.FromReadings(raw.Select(r => r.CpuPercent)),
            FieldAgg.FromReadings(raw.Select(r => r.MemoryPercent)),
            FieldAgg.FromReadings(raw.Select(r => (double?)r.NetInBytesPerSec)),
            FieldAgg.FromReadings(raw.Select(r => (double?)r.NetOutBytesPerSec)),
            FieldAgg.FromReadings(raw.Select(r => r.CpuTempC)));
        _scalarRollup.RebuildMinute(minuteFloorSec, agg);
    }

    public IReadOnlyList<MetricSample> Query(long fromSec, long toSec)
    {
        var scalars = _scalars.Query(fromSec, toSec);
        var gpusByTs = _gpus.Query(fromSec, toSec);
        var fansByTs = _fans.Query(fromSec, toSec);

        var result = new List<MetricSample>(scalars.Count);
        foreach (var s in scalars)
        {
            result.Add(new MetricSample(
                s.TsSec, s.CpuPercent, s.MemoryPercent, s.NetInBytesPerSec, s.NetOutBytesPerSec, s.CpuTempC,
                gpusByTs.TryGetValue(s.TsSec, out var gpus) ? gpus : Array.Empty<GpuReading>(),
                fansByTs.TryGetValue(s.TsSec, out var fans) ? fans : Array.Empty<FanReading>()));
        }
        return result;
    }

    // Mirrors SqliteMetricsHistoryStore.IsRollupEligible exactly: the real
    // ladder never routes a sub-minute step here, but DecimatedHistoryStoreTests
    // calls this directly with arbitrary steps, so the same threshold applies.
    private static bool IsRollupEligible(int stepSeconds) => stepSeconds >= 60 && stepSeconds % 60 == 0;

    public IReadOnlyList<ScalarDecimatedSlot> QueryScalarsDecimated(long fromSec, long toSec, int stepSeconds) =>
        IsRollupEligible(stepSeconds)
            ? _scalarRollup.QueryDecimated(fromSec, toSec, stepSeconds)
            : _scalars.QueryScalarsDecimatedRaw(fromSec, toSec, stepSeconds);

    public IReadOnlyList<GpuDecimatedSlot> QueryGpuDecimated(long fromSec, long toSec, int stepSeconds) =>
        IsRollupEligible(stepSeconds)
            ? _gpus.QueryRollupDecimated(fromSec, toSec, stepSeconds)
            : _gpus.QueryRawDecimated(fromSec, toSec, stepSeconds);

    public IReadOnlyList<FanDecimatedSlot> QueryFanDecimated(long fromSec, long toSec, int stepSeconds) =>
        IsRollupEligible(stepSeconds)
            ? _fans.QueryRollupDecimated(fromSec, toSec, stepSeconds)
            : _fans.QueryRawDecimated(fromSec, toSec, stepSeconds);

    public IReadOnlyList<ComponentTempDecimatedSlot> QueryComponentTempDecimated(long fromSec, long toSec, int stepSeconds) =>
        throw new NotImplementedException("Phase 3: the temperature component ring is not built yet.");

    public IReadOnlyList<TemperatureBucketRow> QueryTemperatureBuckets(long fromUtcMs, long toUtcMs) =>
        throw new NotImplementedException("Phase 3: the temperature bucket rollup is not built yet.");

    public void Dispose()
    {
        _scalars.Dispose();
        _scalarRollup.Dispose();
        _gpus.Dispose();
        _fans.Dispose();
        _superBlock.Dispose();
    }
}
