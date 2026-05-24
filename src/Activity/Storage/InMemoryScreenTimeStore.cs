using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity.Storage;

/// <summary>
/// Non-persistent fallback used in unit tests and as a last-resort stub when the
/// SQLite file can't be opened (e.g. read-only filesystem). Behaviour matches
/// SqliteScreenTimeStore for the same inputs - sessions are bucketed by local
/// start date.
/// </summary>
public sealed class InMemoryScreenTimeStore : IScreenTimeStore
{
    private sealed record Session(string AppName, string? AppPath, long StartedUtc, long EndedUtc, long DurationMs, string DateLocal, int HourLocal);

    private const string DateFormat = "yyyy-MM-dd";
    private readonly object _lock = new();
    private readonly List<Session> _sessions = new();

    public void RecordSession(string appName, string? appPath, long startedUtcMs, long endedUtcMs)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return;
        }
        if (endedUtcMs <= startedUtcMs)
        {
            return;
        }
        var started = DateTimeOffset.FromUnixTimeMilliseconds(startedUtcMs).LocalDateTime;
        var s = new Session(
            appName,
            appPath,
            startedUtcMs,
            endedUtcMs,
            endedUtcMs - startedUtcMs,
            started.ToString(DateFormat),
            started.Hour);
        lock (_lock)
        {
            _sessions.Add(s);
        }
    }

    public DayBreakdown GetDay(DateOnly localDate)
    {
        var dateStr = localDate.ToString(DateFormat);
        var result = new DayBreakdown
        {
            Date = dateStr,
            HourlyMs = new List<long>(new long[24]),
        };
        lock (_lock)
        {
            var dayRows = _sessions.Where(s => s.DateLocal == dateStr).ToList();
            result.Pickups = dayRows.Count;
            result.TotalMs = dayRows.Sum(s => s.DurationMs);
            result.Apps = dayRows
                .GroupBy(s => s.AppName)
                .Select(g => new AppUsage { Name = g.Key, TotalMs = g.Sum(s => s.DurationMs) })
                .OrderByDescending(a => a.TotalMs)
                .ToList();
            foreach (var h in dayRows.GroupBy(s => s.HourLocal))
            {
                if (h.Key >= 0 && h.Key < 24)
                {
                    result.HourlyMs[h.Key] = h.Sum(s => s.DurationMs);
                }
            }
        }
        return result;
    }

    public IReadOnlyList<DayTotal> GetRange(DateOnly fromInclusive, DateOnly toInclusive)
    {
        var from = fromInclusive.ToString(DateFormat);
        var to = toInclusive.ToString(DateFormat);
        lock (_lock)
        {
            return _sessions
                .Where(s => string.CompareOrdinal(s.DateLocal, from) >= 0 && string.CompareOrdinal(s.DateLocal, to) <= 0)
                .GroupBy(s => s.DateLocal)
                .Select(g => new DayTotal
                {
                    Date = g.Key,
                    TotalMs = g.Sum(s => s.DurationMs),
                    Pickups = g.Count(),
                })
                .OrderBy(d => d.Date, StringComparer.Ordinal)
                .ToList();
        }
    }

    public AppHistory GetAppHistory(string appName, DateOnly fromInclusive, DateOnly toInclusive)
    {
        var from = fromInclusive.ToString(DateFormat);
        var to = toInclusive.ToString(DateFormat);
        var result = new AppHistory { AppName = appName };
        lock (_lock)
        {
            var rows = _sessions
                .Where(s => s.AppName == appName
                    && string.CompareOrdinal(s.DateLocal, from) >= 0
                    && string.CompareOrdinal(s.DateLocal, to) <= 0)
                .ToList();
            result.Daily = rows
                .GroupBy(s => s.DateLocal)
                .Select(g => new DayTotal
                {
                    Date = g.Key,
                    TotalMs = g.Sum(s => s.DurationMs),
                    Pickups = g.Count(),
                })
                .OrderBy(d => d.Date, StringComparer.Ordinal)
                .ToList();
            result.TotalMs = rows.Sum(s => s.DurationMs);
            result.TotalPickups = rows.Count;
            result.LongestSessionMs = rows.Count == 0 ? 0 : rows.Max(s => s.DurationMs);
        }
        return result;
    }

    public IReadOnlyList<AppUsage> GetTodayUsage(DateOnly today) => GetDay(today).Apps;

    public IReadOnlyList<AppUsage> GetHourUsage(DateOnly localDate, int hourLocal)
    {
        if (hourLocal < 0 || hourLocal > 23)
        {
            return Array.Empty<AppUsage>();
        }
        var dateStr = localDate.ToString(DateFormat);
        lock (_lock)
        {
            return _sessions
                .Where(s => s.DateLocal == dateStr && s.HourLocal == hourLocal)
                .GroupBy(s => s.AppName)
                .Select(g => new AppUsage { Name = g.Key, TotalMs = g.Sum(s => s.DurationMs) })
                .OrderByDescending(a => a.TotalMs)
                .ToList();
        }
    }

    public int DeleteDay(DateOnly localDate)
    {
        var dateStr = localDate.ToString(DateFormat);
        lock (_lock)
        {
            return _sessions.RemoveAll(s => s.DateLocal == dateStr);
        }
    }

    public int DeleteRange(DateOnly fromInclusive, DateOnly toInclusive)
    {
        var from = fromInclusive.ToString(DateFormat);
        var to = toInclusive.ToString(DateFormat);
        lock (_lock)
        {
            return _sessions.RemoveAll(s =>
                string.CompareOrdinal(s.DateLocal, from) >= 0
                && string.CompareOrdinal(s.DateLocal, to) <= 0);
        }
    }

    public int DeleteApp(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return 0;
        }
        lock (_lock)
        {
            return _sessions.RemoveAll(s => s.AppName == appName);
        }
    }

    public int DeleteAll()
    {
        lock (_lock)
        {
            var n = _sessions.Count;
            _sessions.Clear();
            return n;
        }
    }

    public void Dispose() { }
}
