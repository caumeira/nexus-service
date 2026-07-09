using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Diagnostics.Temperature;

/// <summary>
/// Per-bucket foreground app breakdown for the temperature chart's hover
/// tooltip. Buckets align to the same widthMs grid (bucket start = t / widthMs *
/// widthMs) and are keyed by that slot start, so a hovered chart point maps onto
/// the bucket covering its time. Each bucket lists the apps used within it,
/// most-used first; buckets with no foreground activity are omitted. Pure - no
/// I/O, so the slotting and ranking are unit-testable in isolation.
/// </summary>
public static class ScreenTimeUsage
{
    // The widest tier bucket can touch many apps; cap the list so the tooltip
    // and payload stay bounded. The dropped tail is always the least-used apps.
    private const int MaxAppsPerBucket = 8;

    public static IReadOnlyList<TemperatureAppBucket> Build(
        IReadOnlyList<FocusSessionRow> sessions, long fromUtcMs, long toUtcMs, long slotWidthMs)
    {
        if (sessions.Count == 0 || slotWidthMs <= 0 || toUtcMs <= fromUtcMs)
        {
            return Array.Empty<TemperatureAppBucket>();
        }

        var buckets = new List<TemperatureAppBucket>();
        for (var slotStart = fromUtcMs / slotWidthMs * slotWidthMs; slotStart < toUtcMs; slotStart += slotWidthMs)
        {
            var winStart = Math.Max(slotStart, fromUtcMs);
            var winEnd = Math.Min(slotStart + slotWidthMs, toUtcMs);
            if (winEnd <= winStart)
            {
                continue;
            }

            var apps = AppsInWindow(sessions, winStart, winEnd);
            if (apps.Count == 0)
            {
                continue;
            }

            // Keyed by the slot start (not winStart) so it matches the merged
            // temperature bucket's timestamp on the same grid.
            buckets.Add(new TemperatureAppBucket { StartUtcMs = slotStart, Apps = apps });
        }
        return buckets;
    }

    private static IReadOnlyList<TemperatureAppSlice> AppsInWindow(
        IReadOnlyList<FocusSessionRow> sessions, long winStart, long winEnd)
    {
        var totals = new Dictionary<string, (long Ms, string Name)>(StringComparer.Ordinal);
        foreach (var s in sessions)
        {
            var overlap = Math.Min(s.EndedUtcMs, winEnd) - Math.Max(s.StartedUtcMs, winStart);
            if (overlap <= 0)
            {
                continue;
            }
            // AppId is the path when present (distinguishes two apps that share a
            // display name), otherwise the name.
            var id = string.IsNullOrEmpty(s.AppPath) ? s.AppName : s.AppPath!;
            if (totals.TryGetValue(id, out var prev))
            {
                totals[id] = (prev.Ms + overlap, prev.Name);
            }
            else
            {
                totals[id] = (overlap, s.AppName);
            }
        }
        if (totals.Count == 0)
        {
            return Array.Empty<TemperatureAppSlice>();
        }

        return totals
            .OrderByDescending(kv => kv.Value.Ms)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(MaxAppsPerBucket)
            .Select(kv => new TemperatureAppSlice { AppId = kv.Key, AppName = kv.Value.Name, Ms = kv.Value.Ms })
            .ToList();
    }
}

/// <summary>Wire response for GET /diagnostics/temperatures/apps.</summary>
public sealed record TemperatureAppUsageResponse
{
    public bool Supported { get; init; }
    public int BucketMinutes { get; init; }
    public IReadOnlyList<TemperatureAppBucket> Buckets { get; init; } = Array.Empty<TemperatureAppBucket>();
}

/// <summary>One tier-width bucket (keyed by its slot start, UTC ms) and the apps
/// used within it, most-used first.</summary>
public sealed record TemperatureAppBucket
{
    public long StartUtcMs { get; init; }
    public IReadOnlyList<TemperatureAppSlice> Apps { get; init; } = Array.Empty<TemperatureAppSlice>();
}

/// <summary>One app's foreground time within a bucket.</summary>
public sealed record TemperatureAppSlice
{
    public string AppName { get; init; } = "";
    public string AppId { get; init; } = "";
    public long Ms { get; init; }
}
