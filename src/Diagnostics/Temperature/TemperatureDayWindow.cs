using System;
using System.Globalization;

namespace Nexus.Service.Diagnostics.Temperature;

/// <summary>
/// Pure day-window resolution for the single-day temperature query: parses and
/// validates the date param, then computes the calendar day's UTC bucket range
/// in the given time zone. No I/O; timeZone and nowUtc are injected so the DST
/// and validation edges are unit-testable without depending on the host clock.
/// fromUtcMs/toUtcMs are both inclusive, matching ITemperatureHistoryStore.Query.
/// </summary>
public static class TemperatureDayWindow
{
    public const string DateFormat = "yyyy-MM-dd";

    public static bool TryResolve(
        string date,
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone,
        int retentionDays,
        out long fromUtcMs,
        out long toUtcMs,
        out string? error)
    {
        fromUtcMs = 0;
        toUtcMs = 0;

        if (!DateOnly.TryParseExact(date, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            error = $"invalid date, expected {DateFormat}";
            return false;
        }

        var todayLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, timeZone).DateTime);
        if (day > todayLocal)
        {
            error = "date is in the future";
            return false;
        }
        if (day < todayLocal.AddDays(-retentionDays))
        {
            error = "date is older than the retention window";
            return false;
        }

        // Kind Unspecified so the resolver treats each wall-clock boundary as
        // being in timeZone, not the machine's own local zone.
        var startLocal = new DateTime(day.Year, day.Month, day.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var endLocal = startLocal.AddDays(1);

        fromUtcMs = ResolveDayBoundaryUtc(startLocal, timeZone).ToUnixTimeMilliseconds();
        // Query's upper bound is inclusive and the next day's midnight lands on
        // a raw bucket start, so back off 1 ms to keep that bucket out of this
        // day's range.
        toUtcMs = ResolveDayBoundaryUtc(endLocal, timeZone).ToUnixTimeMilliseconds() - 1;
        error = null;
        return true;
    }

    /// <summary>
    /// UTC instant of a local calendar-day boundary (midnight), resolving the
    /// two DST edges so a 23h spring-forward or 25h fall-back day is still fully
    /// covered: a midnight that falls in a spring-forward gap resolves to the
    /// transition instant (the day's real first moment), and an ambiguous
    /// fall-back midnight resolves to its earlier occurrence.
    /// </summary>
    private static DateTimeOffset ResolveDayBoundaryUtc(DateTime localMidnight, TimeZoneInfo timeZone)
    {
        if (timeZone.IsAmbiguousTime(localMidnight))
        {
            // The larger offset is the earlier UTC instant (UTC = local - offset).
            var offsets = timeZone.GetAmbiguousTimeOffsets(localMidnight);
            var earlier = offsets[0] > offsets[1] ? offsets[0] : offsets[1];
            return new DateTimeOffset(localMidnight, earlier);
        }
        if (timeZone.IsInvalidTime(localMidnight))
        {
            // Midnight doesn't exist; the day begins at the first valid local
            // time past the gap, whose UTC is the transition instant.
            var firstValid = localMidnight;
            while (timeZone.IsInvalidTime(firstValid))
            {
                firstValid = firstValid.AddMinutes(1);
            }
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(firstValid, timeZone));
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localMidnight, timeZone));
    }
}
