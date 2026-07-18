using System;
using System.Collections.Generic;
using System.IO;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Binary-file-backed IMetricsHistoryStore, replacing SqliteMetricsHistoryStore
/// one phase at a time (see the metrics-store design notes). Phase 1 wires
/// only the 1Hz scalar series (cpu/mem/net-in/net-out/cpu-temp) through
/// ScalarRingStore and SuperBlock. Every per-entity series (gpu, fan,
/// storage/RAM temperature) and the long-retention temperature buckets are
/// still SQLite-only: Append silently drops those readings from an incoming
/// MetricSample, Query always returns them as empty lists, and the matching
/// decimated queries throw NotImplementedException noting the phase that
/// adds them.
///
/// Unlike SqliteMetricsHistoryStore, there is no internal write lock: the
/// ring's own crash-safety protocol (see RingFile) is what makes reads
/// lock-free against a concurrent writer, on the same single-writer
/// assumption the whole store is built on (MetricsSampler is the only
/// Append caller).
/// </summary>
public sealed class BinaryMetricsHistoryStore : IMetricsHistoryStore
{
    private const string SuperBlockFileName = "super";
    private const string ScalarsFileName = "scalars.ring";

    private readonly SuperBlock _superBlock;
    private readonly ScalarRingStore _scalars;

    public BinaryMetricsHistoryStore(string dbDir)
    {
        Directory.CreateDirectory(dbDir);
        var scalarCapacity = MetricsHistory.RetentionDays * 86_400L;

        _superBlock = SuperBlock.CreateOrOpen(Path.Combine(dbDir, SuperBlockFileName), scalarCapacity);
        _scalars = new ScalarRingStore(
            Path.Combine(dbDir, ScalarsFileName), scalarCapacity, _superBlock.PruneFloorSec);
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
        }

        if (pruneCutoffSec is { } cutoff && _scalars.RaisePruneFloor(cutoff))
        {
            _superBlock.Persist(_scalars.PruneFloorSec, _superBlock.SourceFloorSec);
        }
    }

    public IReadOnlyList<MetricSample> Query(long fromSec, long toSec)
    {
        var scalars = _scalars.Query(fromSec, toSec);
        var result = new List<MetricSample>(scalars.Count);
        foreach (var s in scalars)
        {
            result.Add(new MetricSample(
                s.TsSec, s.CpuPercent, s.MemoryPercent, s.NetInBytesPerSec, s.NetOutBytesPerSec, s.CpuTempC,
                Array.Empty<GpuReading>(), Array.Empty<FanReading>()));
        }
        return result;
    }

    // Mirrors SqliteMetricsHistoryStore.IsRollupEligible exactly: the real
    // ladder never routes a sub-minute step here, but DecimatedHistoryStoreTests
    // calls this directly with arbitrary steps, so the same threshold applies
    // even though the rollup-eligible branch below isn't implemented yet.
    private static bool IsRollupEligible(int stepSeconds) => stepSeconds >= 60 && stepSeconds % 60 == 0;

    public IReadOnlyList<ScalarDecimatedSlot> QueryScalarsDecimated(long fromSec, long toSec, int stepSeconds)
    {
        if (IsRollupEligible(stepSeconds))
        {
            throw new NotImplementedException("Phase 2: the minute rollup ring is not built yet.");
        }
        return _scalars.QueryScalarsDecimatedRaw(fromSec, toSec, stepSeconds);
    }

    public IReadOnlyList<GpuDecimatedSlot> QueryGpuDecimated(long fromSec, long toSec, int stepSeconds) =>
        throw new NotImplementedException("Phase 2: the gpu entity ring is not built yet.");

    public IReadOnlyList<FanDecimatedSlot> QueryFanDecimated(long fromSec, long toSec, int stepSeconds) =>
        throw new NotImplementedException("Phase 2: the fan entity ring is not built yet.");

    public IReadOnlyList<ComponentTempDecimatedSlot> QueryComponentTempDecimated(long fromSec, long toSec, int stepSeconds) =>
        throw new NotImplementedException("Phase 3: the temperature component ring is not built yet.");

    public IReadOnlyList<TemperatureBucketRow> QueryTemperatureBuckets(long fromUtcMs, long toUtcMs) =>
        throw new NotImplementedException("Phase 3: the temperature bucket rollup is not built yet.");

    public void Dispose()
    {
        _scalars.Dispose();
        _superBlock.Dispose();
    }
}
