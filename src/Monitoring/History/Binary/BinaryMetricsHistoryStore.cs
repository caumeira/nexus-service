using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Binary-file-backed IMetricsHistoryStore, replacing SqliteMetricsHistoryStore
/// one phase at a time (see the metrics-store design notes). Phase 1 wired the
/// 1Hz scalar series (cpu/mem/net-in/net-out/cpu-temp) through ScalarRingStore
/// and SuperBlock; Phase 2 added the scalar minute rollup
/// (ScalarMinuteRollupRing) and per-entity gpu/fan history (GpuRingStore/
/// FanRingStore, each their own raw-second plus minute-rollup rings); Phase 3
/// adds per-storage-drive/RAM-DIMM temperature (TempComponentRingStore) and
/// the unified 90-day cpu/gpu/storage/ram temperature bucket rollup
/// (TempBucketStore), rebuilt from the other rings' raw data on every Append
/// via RebuildTempBuckets - see that method's doc for the source-retention
/// guard mirroring SqliteMetricsHistoryStore.UpsertTempBucketsRollup.
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

    // Real hardware never comes close to this many distinct GPUs, fan
    // channels, or storage/ram temperature components; the cap only exists
    // so the store's footprint stays bounded if it somehow did - see
    // EntityRegistry.RegisterOrGet/TempComponentRegistry.RegisterOrGet.
    private const int EntityCapacity = 32;

    // TempBucketStore's registry spans three domains in one namespace: the
    // single fixed "cpu" key, up to EntityCapacity gpus, and up to
    // EntityCapacity storage/ram components.
    private const int TempBucketEntityCapacity = EntityCapacity * 2 + 1;

    private readonly SuperBlock _superBlock;
    private readonly ScalarRingStore _scalars;
    private readonly ScalarMinuteRollupRing _scalarRollup;
    private readonly GpuRingStore _gpus;
    private readonly FanRingStore _fans;
    private readonly TempComponentRingStore _tempComponents;
    private readonly TempBucketStore _tempBuckets;

    // Oldest ts a temp-bucket rebuild may trust the scalar/gpu/temp-component
    // rings to still hold in full - the binary equivalent of
    // SqliteMetricsHistoryStore's own _sourceFloorSec field, which it
    // recomputes from a live MIN(ts) query at every open. This field instead
    // recovers from SuperBlock.SourceFloorSec, the same cutoff value every
    // ring's own PruneFloorSec recovers from (both are raised together, from
    // the same Append prune cutoff, every time) - so on reopen it is exactly
    // as current as what the rings themselves already hide, never behind it.
    private long? _sourceFloorSec;

    public BinaryMetricsHistoryStore(string dbDir)
    {
        Directory.CreateDirectory(dbDir);
        var secondCapacity = MetricsHistory.RetentionDays * 86_400L;
        var minuteCapacity = MetricsHistory.RetentionDays * 1440L;
        var tempBucketCapacity = MetricsHistory.TempRetentionDays * 86_400L / TempBucketTier.SecondsPerBucket;

        _superBlock = SuperBlock.CreateOrOpen(Path.Combine(dbDir, SuperBlockFileName), secondCapacity);
        _scalars = new ScalarRingStore(Path.Combine(dbDir, ScalarsFileName), secondCapacity, _superBlock.PruneFloorSec);
        _scalarRollup = new ScalarMinuteRollupRing(Path.Combine(dbDir, ScalarsMinuteFileName), minuteCapacity, _superBlock.PruneFloorSec);
        _gpus = new GpuRingStore(Path.Combine(dbDir, "gpu"), secondCapacity, minuteCapacity, EntityCapacity, _superBlock.PruneFloorSec);
        _fans = new FanRingStore(Path.Combine(dbDir, "fan"), secondCapacity, minuteCapacity, EntityCapacity, _superBlock.PruneFloorSec);
        _tempComponents = new TempComponentRingStore(Path.Combine(dbDir, "tempcomponent"), secondCapacity, EntityCapacity, _superBlock.PruneFloorSec);
        _tempBuckets = new TempBucketStore(
            Path.Combine(dbDir, "tempbucket"), tempBucketCapacity, TempBucketEntityCapacity, ComputeTempBucketFloorSec(_superBlock.PruneFloorSec));
        _sourceFloorSec = _superBlock.SourceFloorSec;
    }

    // temp_buckets keeps MetricsHistory.TempRetentionDays instead of the
    // RetentionDays scalarPruneFloorSec encodes, so shift it back by the gap
    // between the two retention windows - mirrors
    // SqliteMetricsHistoryStore.Prune's inline tempCutoffSec computation
    // exactly. The RingFile.UnwrittenStamp sentinel (meaning "never pruned")
    // passes through unchanged rather than underflowing.
    private static long ComputeTempBucketFloorSec(long scalarPruneFloorSec) =>
        scalarPruneFloorSec == RingFile.UnwrittenStamp
            ? RingFile.UnwrittenStamp
            : scalarPruneFloorSec - (MetricsHistory.TempRetentionDays - MetricsHistory.RetentionDays) * 86_400L;

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
            _tempComponents.Append(samples);

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

            RebuildTempBuckets(samples);
            _tempBuckets.Flush();
        }

        if (pruneCutoffSec is { } cutoff)
        {
            var changed = _scalars.RaisePruneFloor(cutoff);
            changed |= _scalarRollup.RaisePruneFloor(cutoff);
            changed |= _gpus.RaisePruneFloor(cutoff);
            changed |= _fans.RaisePruneFloor(cutoff);
            changed |= _tempComponents.RaisePruneFloor(cutoff);

            var newSourceFloor = Math.Max(_sourceFloorSec ?? long.MinValue, cutoff);
            var sourceFloorChanged = newSourceFloor != _sourceFloorSec;
            _sourceFloorSec = newSourceFloor;

            changed |= _tempBuckets.RaisePruneFloor(ComputeTempBucketFloorSec(cutoff));

            if (changed || sourceFloorChanged)
            {
                _superBlock.Persist(cutoff, _sourceFloorSec);
            }
        }
    }

    // Rebuilds every (key, bucket) pair this batch touched in the unified
    // cpu/gpu/storage/ram temperature bucket rollup, mirroring
    // SqliteMetricsHistoryStore.UpsertTempBucketsRollup: a bucket whose
    // start has aged past _sourceFloorSec is excluded from the touched sets
    // entirely (never rebuilt, never clobbered by a partial or empty
    // rebuild), matching that method's WithinSourceRetention guard.
    private void RebuildTempBuckets(IReadOnlyList<MetricSample> samples)
    {
        var width = TempBucketTier.SecondsPerBucket;
        bool WithinSourceRetention(long bucketStart) => _sourceFloorSec is not { } floor || bucketStart >= floor;

        var cpuName = "CPU";
        foreach (var s in samples)
        {
            if (!string.IsNullOrEmpty(s.CpuName))
            {
                cpuName = s.CpuName;
                break;
            }
        }

        var cpuBuckets = new HashSet<long>();
        var gpuPairs = new HashSet<(long Bucket, string GpuId)>();
        var gpuNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var componentPairs = new HashSet<(long Bucket, string ComponentId)>();
        var componentInfo = new Dictionary<string, (string Kind, string Name)>(StringComparer.Ordinal);

        foreach (var s in samples)
        {
            var bucket = s.TsSec / width * width;
            if (!WithinSourceRetention(bucket))
            {
                continue;
            }

            cpuBuckets.Add(bucket);
            foreach (var g in s.Gpus)
            {
                gpuPairs.Add((bucket, g.GpuId));
                gpuNames[g.GpuId] = g.Name;
            }
            foreach (var c in s.ComponentTemps)
            {
                componentPairs.Add((bucket, c.ComponentId));
                componentInfo[c.ComponentId] = (c.Kind, c.Name);
            }
        }

        foreach (var bucket in cpuBuckets)
        {
            var rows = _scalars.Query(bucket, bucket + width - 1);
            var agg = FieldAgg.FromReadings(rows.Select(r => r.CpuTempC));
            _tempBuckets.RebuildBucket("cpu", "cpu", cpuName, bucket, agg);
        }

        foreach (var bucketGroup in gpuPairs.GroupBy(p => p.Bucket))
        {
            var bucket = bucketGroup.Key;
            var byTs = _gpus.Query(bucket, bucket + width - 1);
            var byGpu = new Dictionary<string, List<double?>>(StringComparer.Ordinal);
            foreach (var readings in byTs.Values)
            {
                foreach (var g in readings)
                {
                    if (!byGpu.TryGetValue(g.GpuId, out var values))
                    {
                        values = new List<double?>();
                        byGpu[g.GpuId] = values;
                    }
                    values.Add(g.TempC);
                }
            }

            foreach (var (_, gpuId) in bucketGroup)
            {
                var agg = byGpu.TryGetValue(gpuId, out var values) ? FieldAgg.FromReadings(values) : FieldAgg.Empty;
                _tempBuckets.RebuildBucket("gpu:" + gpuId, "gpu", gpuNames[gpuId], bucket, agg);
            }
        }

        foreach (var bucketGroup in componentPairs.GroupBy(p => p.Bucket))
        {
            var bucket = bucketGroup.Key;
            var byTs = _tempComponents.Query(bucket, bucket + width - 1);
            var byComponent = new Dictionary<string, List<double?>>(StringComparer.Ordinal);
            foreach (var readings in byTs.Values)
            {
                foreach (var c in readings)
                {
                    if (!byComponent.TryGetValue(c.ComponentId, out var values))
                    {
                        values = new List<double?>();
                        byComponent[c.ComponentId] = values;
                    }
                    values.Add(c.ValueC);
                }
            }

            foreach (var (_, componentId) in bucketGroup)
            {
                var agg = byComponent.TryGetValue(componentId, out var values) ? FieldAgg.FromReadings(values) : FieldAgg.Empty;
                var (kind, name) = componentInfo[componentId];
                _tempBuckets.RebuildBucket(componentId, kind, name, bucket, agg);
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
        var componentsByTs = _tempComponents.Query(fromSec, toSec);

        var result = new List<MetricSample>(scalars.Count);
        foreach (var s in scalars)
        {
            result.Add(new MetricSample(
                s.TsSec, s.CpuPercent, s.MemoryPercent, s.NetInBytesPerSec, s.NetOutBytesPerSec, s.CpuTempC,
                gpusByTs.TryGetValue(s.TsSec, out var gpus) ? gpus : Array.Empty<GpuReading>(),
                fansByTs.TryGetValue(s.TsSec, out var fans) ? fans : Array.Empty<FanReading>())
            {
                ComponentTemps = componentsByTs.TryGetValue(s.TsSec, out var comps) ? comps : Array.Empty<ComponentTempReading>(),
            });
        }
        return result;
    }

    // Mirrors SqliteMetricsHistoryStore.IsRollupEligible exactly: the real
    // ladder never routes a sub-minute step here, but ScalarDecimatedRawSpec
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

    // No temp-component minute rollup exists (see TempComponentRingStore's
    // class doc), so this always aggregates the raw ring directly, unlike
    // QueryGpuDecimated/QueryFanDecimated's rollup-eligible fast path.
    public IReadOnlyList<ComponentTempDecimatedSlot> QueryComponentTempDecimated(long fromSec, long toSec, int stepSeconds) =>
        _tempComponents.QueryRawDecimated(fromSec, toSec, stepSeconds);

    // fromUtcMs/toUtcMs are milliseconds; TempBucketStore's own keys are
    // seconds, so both bounds are floor-divided rather than rounded - a
    // window boundary landing mid-second still includes that second's
    // bucket, matching SqliteMetricsHistoryStore.QueryTemperatureBuckets.
    public IReadOnlyList<TemperatureBucketRow> QueryTemperatureBuckets(long fromUtcMs, long toUtcMs) =>
        _tempBuckets.Query(fromUtcMs / 1000, toUtcMs / 1000);

    public void Dispose()
    {
        _scalars.Dispose();
        _scalarRollup.Dispose();
        _gpus.Dispose();
        _fans.Dispose();
        _tempComponents.Dispose();
        _tempBuckets.Dispose();
        _superBlock.Dispose();
    }
}
