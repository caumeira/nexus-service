using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Activity;
using Nexus.Service.Sensors;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Production IAppUsageSource: reduces the already-live ProcessMonitor /
/// GpuProcessMonitor snapshots to every app above
/// MetricsHistory.AppUsageEpsilon for each metric, capped at
/// MetricsHistory.TopAppsPerSample. Both monitors already sample on their
/// own always-on loop (kept alive via SetDemand); this only reads their
/// latest snapshot and aggregates by process name (ProcessMonitor is
/// per-pid on Windows, not grouped - see ProcessAggregation). GPU
/// per-process entries are attributed to a metric id ("gpu:&lt;gid&gt;")
/// via the same AdapterLuid the scalar gpu series already resolves -
/// which means re-reading ISensorProvider.GetGpus() every tick, the same
/// call SystemMetricsSource already makes every second; LhmComputer's own
/// throttle absorbs the actual hardware re-poll, so this adds no new
/// hardware I/O, only a redundant in-process LUID-matching pass. Each
/// adapter also contributes a "vram:&lt;gid&gt;" sample, ranked by
/// DedicatedMb rather than GpuPercent - a process can hold significant
/// VRAM while nearly idle, so the two rankings can select different apps.
/// </summary>
public sealed class ProcessAppUsageSource : IAppUsageSource
{
    private readonly ProcessMonitor _processes;
    private readonly GpuProcessMonitor _gpuProcesses;
    private readonly ISensorProvider _sensors;

    private const string DemandSource = "app-usage-history";

    public ProcessAppUsageSource(ProcessMonitor processes, GpuProcessMonitor gpuProcesses, ISensorProvider sensors)
    {
        _processes = processes;
        _gpuProcesses = gpuProcesses;
        _sensors = sensors;

        // Per-app usage recording is always-on regardless of WebSocket
        // subscribers - both monitors self-gate their own sampling loop on
        // demand, so this keeps them running for the lifetime of the
        // service without touching their existing subscription-gated path.
        _processes.SetDemand(DemandSource, true);
        _gpuProcesses.SetDemand(DemandSource, true);
    }

    public IReadOnlyList<AppMetricSample> Sample()
    {
        var result = new List<AppMetricSample>();

        var procs = _processes.GetProcesses();
        if (procs.Count > 0)
        {
            var grouped = ProcessAggregation.GroupByName(procs).Values;

            var cpuTop = grouped
                .Where(a => a.CpuPercent > MetricsHistory.AppUsageEpsilon)
                .OrderByDescending(a => a.CpuPercent)
                .Take(MetricsHistory.TopAppsPerSample)
                .Select(a => new AppUsagePoint(a.Name, a.CpuPercent, null))
                .ToList();
            result.Add(new AppMetricSample("cpu", cpuTop));

            var memTop = grouped
                .Where(a => a.MemoryMb > MetricsHistory.AppUsageEpsilon)
                .OrderByDescending(a => a.MemoryMb)
                .Take(MetricsHistory.TopAppsPerSample)
                .Select(a => new AppUsagePoint(a.Name, a.MemoryMb, null))
                .ToList();
            result.Add(new AppMetricSample("memory", memTop));
        }

        var gpuEntries = _gpuProcesses.GetSnapshot();
        if (gpuEntries.Count > 0)
        {
            var luidToGpuId = BuildLuidToGpuId();
            foreach (var group in gpuEntries.GroupBy(e => e.AdapterLuid))
            {
                if (!luidToGpuId.TryGetValue(group.Key, out var gid))
                {
                    continue;
                }
                var top = group
                    .OrderByDescending(e => e.GpuPercent)
                    .Take(MetricsHistory.TopAppsPerSample)
                    .Select(e => new AppUsagePoint(e.Name, e.GpuPercent, e.DedicatedMb))
                    .ToList();
                result.Add(new AppMetricSample($"gpu:{gid}", top));

                var vramTop = group
                    .Where(e => e.DedicatedMb > MetricsHistory.AppUsageEpsilon)
                    .OrderByDescending(e => e.DedicatedMb)
                    .Take(MetricsHistory.TopAppsPerSample)
                    .Select(e => new AppUsagePoint(e.Name, e.DedicatedMb, null))
                    .ToList();
                result.Add(new AppMetricSample($"vram:{gid}", vramTop));
            }
        }

        return result;
    }

    private Dictionary<string, string> BuildLuidToGpuId()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in _sensors.GetGpus())
        {
            if (!string.IsNullOrEmpty(g.AdapterLuid))
            {
                map[g.AdapterLuid] = MetricsHistory.SanitizeId(g.Id);
            }
        }
        return map;
    }
}
