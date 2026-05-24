using System;
using System.Collections.Generic;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity.Storage;

/// <summary>
/// Persistent per-app focus history shared across all platform IScreenTimeProvider
/// implementations. Providers call RecordSession when a focus session ends; reads
/// power both the legacy /api/screentime/history "today" endpoint and the new
/// /api/screentime/{day,range,app} browse endpoints.
///
/// Sessions are bucketed into local-time days at write time (the wall-clock day
/// the session *started*), so reads never need timezone math and DST transitions
/// land in the bucket the user actually saw on their clock.
/// </summary>
public interface IScreenTimeStore : IDisposable
{
    void RecordSession(string appName, string? appPath, long startedUtcMs, long endedUtcMs);

    DayBreakdown GetDay(DateOnly localDate);
    IReadOnlyList<DayTotal> GetRange(DateOnly fromInclusive, DateOnly toInclusive);
    AppHistory GetAppHistory(string appName, DateOnly fromInclusive, DateOnly toInclusive);
    IReadOnlyList<AppUsage> GetTodayUsage(DateOnly today);
    IReadOnlyList<AppUsage> GetHourUsage(DateOnly localDate, int hourLocal);

    int DeleteDay(DateOnly localDate);
    int DeleteRange(DateOnly fromInclusive, DateOnly toInclusive);
    int DeleteApp(string appName);
    int DeleteAll();
}
