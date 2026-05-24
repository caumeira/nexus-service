using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Activity;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Serialization;

namespace Nexus.Service.Widgets.WidgetActions;

/// <summary>
/// Host actions surfacing screen-time data to declarative widgets.
/// Wraps <see cref="IScreenTimeProvider"/> so widgets read a flat
/// payload via /widgets-api/dispatch. Time strings are pre-formatted
/// ("Xh Ym") so widget manifests bind ready-to-render values without
/// needing a time-formatting function in the renderer.
/// </summary>
public static class ScreentimeActions
{
    public static void RegisterAll(WidgetActionRegistry registry)
    {
        registry.Register("screentime.today", (services, _, _) =>
        {
            var provider = services.GetRequiredService<IScreenTimeProvider>();
            var session = provider.GetCurrentSession();
            var rawHistory = provider.GetTodayUsage();

            var sortedHistory = rawHistory
                .OrderByDescending(a => a.TotalMs)
                .ToList();
            long totalMs = 0;
            foreach (var a in sortedHistory) totalMs += a.TotalMs;
            long maxMs = sortedHistory.Count > 0 ? sortedHistory[0].TotalMs : 1;

            var historyOut = sortedHistory.Select(a => new ScreentimeHistoryEntryDto
            {
                Name = a.Name,
                TotalMs = a.TotalMs,
                Formatted = FormatHm(a.TotalMs),
                PctOfMax = maxMs > 0 ? (int)Math.Round(a.TotalMs * 100.0 / maxMs) : 0,
            }).ToList();

            var dto = new ScreentimeTodayDto
            {
                Focus = (session is null || string.IsNullOrEmpty(session.Name))
                    ? null
                    : new ScreentimeFocusDto
                    {
                        Name = session.Name,
                        TotalMs = session.Today.Total,
                        Formatted = FormatHm(session.Today.Total),
                    },
                History = historyOut,
                TotalMs = totalMs,
                TotalFormatted = FormatHm(totalMs),
                MaxMs = maxMs,
                HasData = historyOut.Count > 0,
            };

            // AOT-safe serialisation. Anonymous-type serialisation goes
            // through reflection which the trimmer strips, so we MUST
            // round-trip through a typed DTO registered in AppJsonContext.
            var json = JsonSerializer.Serialize(dto, AppJsonContext.Default.ScreentimeTodayDto);
            using var doc = JsonDocument.Parse(json);
            return System.Threading.Tasks.Task.FromResult<JsonElement?>(doc.RootElement.Clone());
        });
    }

    /// <summary>"3h 12m" / "12m" / "0m" — same formatter the legacy widget used.</summary>
    private static string FormatHm(long totalMs)
    {
        var totalMin = (int)Math.Round(totalMs / 60000.0);
        var h = totalMin / 60;
        var m = totalMin % 60;
        if (h == 0) return $"{m}m";
        if (m == 0) return $"{h}h";
        return $"{h}h {m}m";
    }

    public static IReadOnlyList<string> AllActions => new[] { "screentime.today" };
}
