using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Diagnostics.Gpu;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Diagnostics.Temperature;

/// <summary>
/// Samples CPU/GPU/storage/RAM temperatures on its own tick cadence from
/// existing read paths (no new hardware I/O) and flushes one bucket per
/// component to ITemperatureHistoryStore on rollover. Mirrors
/// HeartbeatService's PeriodicTimer do/while shape with a per-tick try/catch.
/// </summary>
public sealed class TemperatureSampler : BackgroundService
{
    /// <summary>Bucket width in minutes; shared with TemperatureInsights for
    /// bucket-adjacency checks and with the route response's bucketMinutes field.</summary>
    public const int BucketMinutes = 5;

    private const long BucketMs = BucketMinutes * 60_000L;

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(90);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromDays(1);

    // Bounds the wait for ISensorProvider.ReadyAsync so a platform whose
    // hardware enumeration never signals ready still starts sampling.
    private static readonly TimeSpan StartupReadyTimeout = TimeSpan.FromSeconds(60);

    private readonly ISensorProvider _sensors;
    private readonly GpuHealthMonitor _gpu;
    private readonly SmartHealthMonitor _smart;
    private readonly ITemperatureHistoryStore _store;
    private readonly TemperatureBucketAccumulator _accumulator = new();

    private string? _cpuName;
    private DateTime _lastPruneUtc = DateTime.MinValue;

    public TemperatureSampler(
        ISensorProvider sensors, GpuHealthMonitor gpu, SmartHealthMonitor smart, ITemperatureHistoryStore store)
    {
        _sensors = sensors;
        _gpu = gpu;
        _smart = smart;
        _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _sensors.ReadyAsync(stoppingToken).WaitAsync(StartupReadyTimeout, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (TimeoutException)
        {
            // Sensor enumeration is still warming up; sample anyway on the
            // schedule below rather than block forever.
        }

        using var timer = new PeriodicTimer(TickInterval);
        do
        {
            try
            {
                Tick(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[temp-sampler] tick failed: {ex.Message}");
            }
        } while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
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
            ServiceLog.Warn($"[temp-sampler] prune failed: {ex.Message}");
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
