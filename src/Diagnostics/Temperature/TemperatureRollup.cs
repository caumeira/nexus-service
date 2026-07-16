using System;
using System.Collections.Generic;
using Nexus.Service.Diagnostics.Gpu;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Diagnostics.Temperature;

/// <summary>
/// Rolls up CPU/GPU/storage/RAM temperatures into 5-minute buckets and
/// flushes one bucket per component to ITemperatureHistoryStore on rollover.
/// Absorbed by MetricsSampler as the 90-day temperature tier alongside the
/// 1Hz/7-day metrics.db series: MetricsSampler drives Tick() every 30th tick
/// (its own flush cadence) instead of this class running its own
/// BackgroundService loop.
/// </summary>
public sealed class TemperatureRollup
{
    /// <summary>Bucket width in minutes; shared with TemperatureInsights for
    /// bucket-adjacency checks and with the route response's bucketMinutes field.</summary>
    public const int BucketMinutes = 5;

    /// <summary>Retention window in days; shared with the route response's
    /// retentionDays field and the single-day query's oldest-allowed-date check.</summary>
    public const int RetentionDays = 90;

    private const long BucketMs = BucketMinutes * 60_000L;

    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(RetentionDays);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromDays(1);

    private readonly ISensorProvider _sensors;
    private readonly GpuHealthMonitor _gpu;
    private readonly SmartHealthMonitor _smart;
    private readonly ITemperatureHistoryStore _store;
    private readonly TemperatureBucketAccumulator _accumulator = new();

    private string? _cpuName;
    private DateTime _lastPruneUtc = DateTime.MinValue;

    public TemperatureRollup(
        ISensorProvider sensors, GpuHealthMonitor gpu, SmartHealthMonitor smart, ITemperatureHistoryStore store)
    {
        _sensors = sensors;
        _gpu = gpu;
        _smart = smart;
        _store = store;
    }

    /// <summary>Re-keys legacy enumeration-index GPU temperature rows to the
    /// current UUID-based id (GpuComponentIdMigration). Runs once, called by
    /// MetricsSampler after its bounded ISensorProvider.ReadyAsync wait so
    /// GPU UUIDs are resolvable.</summary>
    public void RunStartupMigration()
    {
        try
        {
            GpuComponentIdMigration.Migrate(_store, ResolveGpusWithUuid());
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[temp-rollup] gpu id migration failed: {ex.Message}");
        }
    }

    internal void Tick(DateTime nowUtc)
    {
        var bucketStartMs = AlignBucket(new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds());
        var flushed = _accumulator.Advance(bucketStartMs, ReadComponents());
        if (flushed.Count > 0)
        {
            _store.UpsertBuckets(flushed);
        }

        MaybePrune(nowUtc);
    }

    private static long AlignBucket(long nowMs) => nowMs / BucketMs * BucketMs;

    private IReadOnlyList<(string Name, string Uuid)> ResolveGpusWithUuid()
    {
        var snap = _gpu.Snapshot();
        if (!snap.Supported)
        {
            return Array.Empty<(string, string)>();
        }

        var result = new List<(string, string)>();
        foreach (var g in snap.Gpus)
        {
            if (g.Uuid is { Length: > 0 } uuid)
            {
                result.Add((g.Name, uuid));
            }
        }
        return result;
    }

    private void MaybePrune(DateTime nowUtc)
    {
        if (nowUtc - _lastPruneUtc < PruneInterval)
        {
            return;
        }
        _lastPruneUtc = nowUtc;
        try
        {
            var cutoffMs = new DateTimeOffset(nowUtc - RetentionWindow).ToUnixTimeMilliseconds();
            _store.PruneOlderThan(cutoffMs);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[temp-rollup] prune failed: {ex.Message}");
        }
    }

    private IEnumerable<(string Id, string Kind, string Name, double ValueC)> ReadComponents()
    {
        var cpuTemp = SummarySensors.Value(_sensors, SummarySensorKind.CpuTemp);
        if (cpuTemp is { } cpuC)
        {
            yield return ("cpu", "cpu", ResolveCpuName(), cpuC);
        }

        var gpuSnap = _gpu.Snapshot();
        if (gpuSnap.Supported)
        {
            for (var i = 0; i < gpuSnap.Gpus.Count; i++)
            {
                var g = gpuSnap.Gpus[i];
                if (g.TemperatureC is { } gpuC)
                {
                    // NVML's UUID survives reboots and driver updates; the
                    // enumeration index does not, so it is a fallback for the
                    // rare case UUID is unsupported, not the primary id.
                    var id = g.Uuid is { Length: > 0 } uuid ? $"gpu:{uuid}" : $"gpu:{i}";
                    yield return (id, "gpu", g.Name, gpuC);
                }
            }
        }

        var smartSnap = _smart.Snapshot();
        if (smartSnap.Supported)
        {
            foreach (var drive in smartSnap.Drives)
            {
                if (drive.TemperatureC is { } driveC)
                {
                    yield return (drive.Id, "storage", drive.Name, driveC);
                }
            }
        }

        var ramIndex = 0;
        foreach (var sensor in _sensors.GetMemorySensors())
        {
            if (!string.Equals(sensor.Type, "Temperature", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // Most boards expose no memory-class temperature sensor at all;
            // that absence is normal, so no ram series is emitted for them.
            if (!sensor.Name.Contains("dimm", StringComparison.OrdinalIgnoreCase)
                && !sensor.Name.Contains("memory", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            yield return ($"ram:{ramIndex}", "ram", sensor.Name, sensor.Value);
            ramIndex++;
        }
    }

    private string ResolveCpuName()
    {
        if (_cpuName is not null)
        {
            return _cpuName;
        }
        try
        {
            var model = _sensors.GetCpuModel();
            _cpuName = string.IsNullOrWhiteSpace(model) ? "CPU" : model;
        }
        catch
        {
            _cpuName = "CPU";
        }
        return _cpuName;
    }
}
