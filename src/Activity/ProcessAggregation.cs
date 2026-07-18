using System;
using System.Collections.Generic;

namespace Nexus.Service.Activity;

/// <summary>One process name's aggregate across every pid ProcessMonitor
/// currently reports under that name. StartedAtMs is the NEWEST instance's
/// creation time, not the oldest - a relaunched app should read as freshly
/// started even while an older instance of the same name lingers. HasWindow
/// is true if any instance under the name owns a visible top-level window -
/// Task-Manager-style App vs Background classification.</summary>
public sealed record ProcessNameAggregate(
    string Name, double CpuPercent, double MemoryMb, long? StartedAtMs, bool HasWindow, double StorageBytesPerSec,
    double StorageReadBytesPerSec = 0, double StorageWriteBytesPerSec = 0);

/// <summary>
/// Groups ProcessMonitor's per-pid snapshot (Windows never aggregates by
/// name - each pid is its own row) by process name. Shared by
/// ProcessAppUsageSource (top-N app selection per metric) and
/// MonitoringHistoryRoutes (resolving a historical app row's live
/// startedAtMs by name) so the two stay consistent about what "this app" means.
/// </summary>
public static class ProcessAggregation
{
    public static IReadOnlyDictionary<string, ProcessNameAggregate> GroupByName(IReadOnlyList<ProcessInfo> procs)
    {
        // OrdinalIgnoreCase matches ProcessMonitor.ResolveExecutablePath's
        // name comparison - the same process name observed with differing
        // case across ticks must aggregate as one app, not fragment.
        var map = new Dictionary<string, ProcessNameAggregate>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in procs)
        {
            if (map.TryGetValue(p.Name, out var acc))
            {
                map[p.Name] = acc with
                {
                    CpuPercent = acc.CpuPercent + p.CpuPercent,
                    MemoryMb = acc.MemoryMb + p.MemoryMb,
                    StartedAtMs = NewestOf(acc.StartedAtMs, p.StartedAtMs),
                    HasWindow = acc.HasWindow || p.HasWindow,
                    StorageBytesPerSec = acc.StorageBytesPerSec + p.StorageBytesPerSec,
                    StorageReadBytesPerSec = acc.StorageReadBytesPerSec + p.StorageReadBytesPerSec,
                    StorageWriteBytesPerSec = acc.StorageWriteBytesPerSec + p.StorageWriteBytesPerSec,
                };
            }
            else
            {
                map[p.Name] = new ProcessNameAggregate(
                    p.Name, p.CpuPercent, p.MemoryMb, p.StartedAtMs, p.HasWindow, p.StorageBytesPerSec,
                    p.StorageReadBytesPerSec, p.StorageWriteBytesPerSec);
            }
        }
        return map;
    }

    private static long? NewestOf(long? a, long? b)
    {
        if (a is null)
        {
            return b;
        }
        if (b is null)
        {
            return a;
        }
        return Math.Max(a.Value, b.Value);
    }
}
