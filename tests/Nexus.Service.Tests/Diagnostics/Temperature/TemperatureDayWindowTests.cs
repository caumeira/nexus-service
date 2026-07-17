using System;
using Nexus.Service.Diagnostics.Temperature;
using Nexus.Service.Monitoring.History;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Temperature;

public class TemperatureDayWindowTests
{
    private const int RetentionDays = 90;
    private static readonly DateTimeOffset FixedNowUtc = new(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);

    private static TimeZoneInfo FixedOffsetZone(double hours) =>
        TimeZoneInfo.CreateCustomTimeZone($"fixed{hours}", TimeSpan.FromHours(hours), "Fixed", "Fixed");

    // Synthetic zone (not tied to any real IANA/Windows id) with US-like spring
    // forward on 2026-03-08 and fall back on 2026-11-01, base offset -5h.
    private static TimeZoneInfo BuildDstZone()
    {
        var toDst = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 8);
        var toStandard = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31),
            TimeSpan.FromHours(1), toDst, toStandard);
        return TimeZoneInfo.CreateCustomTimeZone(
            "test-dst", TimeSpan.FromHours(-5), "Test DST", "Test Standard", "Test Daylight", new[] { rule });
    }

    // Spring forward exactly at midnight (04-05), so local midnight on the
    // transition date has no UTC equivalent (a real, if rare, zone convention).
    private static TimeZoneInfo BuildMidnightGapZone()
    {
        var toDst = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 4, 5);
        var toStandard = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 10, 5);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31),
            TimeSpan.FromHours(1), toDst, toStandard);
        return TimeZoneInfo.CreateCustomTimeZone(
            "test-midnight-gap", TimeSpan.FromHours(-5), "Test Gap", "Test Standard", "Test Daylight", new[] { rule });
    }

    // Falls back at 01:00 (11-01), so [00:00, 01:00) that day occurs twice and
    // local midnight itself is ambiguous.
    private static TimeZoneInfo BuildMidnightAmbiguousZone()
    {
        var toDst = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 8);
        var toStandard = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 11, 1);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31),
            TimeSpan.FromHours(1), toDst, toStandard);
        return TimeZoneInfo.CreateCustomTimeZone(
            "test-midnight-ambiguous", TimeSpan.FromHours(-5), "Test Amb", "Test Standard", "Test Daylight", new[] { rule });
    }

    [Fact]
    public void TryResolve_Utc_ReturnsExactCalendarDayRange()
    {
        var ok = TemperatureDayWindow.TryResolve(
            "2026-07-01", FixedNowUtc, TimeZoneInfo.Utc, RetentionDays, out var fromMs, out var toMs, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), fromMs);
        // toMs is inclusive (matches ITemperatureHistoryStore.Query), so it is
        // the last millisecond of the day, not the next day's midnight.
        Assert.Equal(new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() - 1, toMs);
    }

    [Fact]
    public void TryResolve_FixedPositiveOffset_ShiftsRangeByTheOffset()
    {
        var zone = FixedOffsetZone(10);

        var ok = TemperatureDayWindow.TryResolve(
            "2026-06-15", FixedNowUtc, zone, RetentionDays, out var fromMs, out var toMs, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(new DateTimeOffset(2026, 6, 14, 14, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), fromMs);
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 14, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() - 1, toMs);
        Assert.Equal(24 * 3_600_000L - 1, toMs - fromMs);
    }

    [Fact]
    public void TryResolve_SpringForwardDay_WindowIsTwentyThreeHours()
    {
        var zone = BuildDstZone();
        var nowInsideWindow = new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero);

        var ok = TemperatureDayWindow.TryResolve(
            "2026-03-08", nowInsideWindow, zone, RetentionDays, out var fromMs, out var toMs, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(23 * 3_600_000L - 1, toMs - fromMs);
    }

    [Fact]
    public void TryResolve_FallBackDay_WindowIsTwentyFiveHours()
    {
        var zone = BuildDstZone();
        var nowInsideWindow = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);

        var ok = TemperatureDayWindow.TryResolve(
            "2026-11-01", nowInsideWindow, zone, RetentionDays, out var fromMs, out var toMs, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(25 * 3_600_000L - 1, toMs - fromMs);
    }

    [Fact]
    public void TryResolve_ToUtcMs_ExcludesTheFirstBucketOfTheNextDay()
    {
        var ok = TemperatureDayWindow.TryResolve(
            "2026-07-01", FixedNowUtc, TimeZoneInfo.Utc, RetentionDays, out var fromMs, out var toMs, out _);
        Assert.True(ok);

        // IMetricsHistoryStore.QueryTemperatureBuckets is inclusive on both
        // ends; a minute bucket lands exactly at the next day's midnight
        // under continuous sampling, so toMs must fall short of it or that
        // bucket leaks in.
        var nextDayFirstBucketMs = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var store = new InMemoryMetricsHistoryStore();
        store.Append(new[]
        {
            new MetricSample(nextDayFirstBucketMs / 1000, null, null, null, null, 50,
                Array.Empty<GpuReading>(), Array.Empty<FanReading>()),
        }, null);

        var rows = store.QueryTemperatureBuckets(fromMs, toMs);

        Assert.Empty(rows);
    }

    [Fact]
    public void TryResolve_SpringForwardAtMidnight_ReturnsTwentyThreeHourDay()
    {
        var zone = BuildMidnightGapZone();
        var nowInsideWindow = new DateTimeOffset(2026, 4, 6, 0, 0, 0, TimeSpan.Zero);

        var ok = TemperatureDayWindow.TryResolve(
            "2026-04-05", nowInsideWindow, zone, RetentionDays, out var fromMs, out var toMs, out var error);

        Assert.True(ok);
        Assert.Null(error);
        // The nonexistent midnight resolves to the transition instant, so the
        // day is 23h rather than being rejected.
        Assert.Equal(23 * 3_600_000L - 1, toMs - fromMs);
    }

    [Fact]
    public void TryResolve_DayBeforeMidnightSpringForward_ReturnsFullDay()
    {
        var zone = BuildMidnightGapZone();
        var nowInsideWindow = new DateTimeOffset(2026, 4, 6, 0, 0, 0, TimeSpan.Zero);

        // 04-04 is a normal 24h day; its end boundary (04-05 midnight) is the
        // one that falls in the gap, which must not reject the request.
        var ok = TemperatureDayWindow.TryResolve(
            "2026-04-04", nowInsideWindow, zone, RetentionDays, out var fromMs, out var toMs, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(24 * 3_600_000L - 1, toMs - fromMs);
    }

    [Fact]
    public void TryResolve_FallBackAtMidnight_IncludesTheRepeatedFirstHour()
    {
        var zone = BuildMidnightAmbiguousZone();
        var nowInsideWindow = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);

        var ok = TemperatureDayWindow.TryResolve(
            "2026-11-01", nowInsideWindow, zone, RetentionDays, out var fromMs, out var toMs, out var error);

        Assert.True(ok);
        Assert.Null(error);
        // The day starts at the earlier occurrence of the ambiguous midnight,
        // so it spans 25h with its first hour included, not dropped.
        Assert.Equal(25 * 3_600_000L - 1, toMs - fromMs);
    }

    [Theory]
    [InlineData("2026-13-01")]
    [InlineData("07/01/2026")]
    [InlineData("2026-07-1")]
    [InlineData("not-a-date")]
    [InlineData("")]
    public void TryResolve_MalformedDate_Fails(string date)
    {
        var ok = TemperatureDayWindow.TryResolve(
            date, FixedNowUtc, TimeZoneInfo.Utc, RetentionDays, out _, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryResolve_TodayLocal_IsAllowed()
    {
        var ok = TemperatureDayWindow.TryResolve(
            "2026-07-07", FixedNowUtc, TimeZoneInfo.Utc, RetentionDays, out _, out _, out var error);

        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void TryResolve_FutureDate_Fails()
    {
        var ok = TemperatureDayWindow.TryResolve(
            "2026-07-08", FixedNowUtc, TimeZoneInfo.Utc, RetentionDays, out _, out _, out var error);

        Assert.False(ok);
        Assert.Contains("future", error);
    }

    [Fact]
    public void TryResolve_ExactlyAtRetentionBoundary_IsAllowed()
    {
        var boundaryDate = DateOnly.FromDateTime(FixedNowUtc.DateTime).AddDays(-RetentionDays);

        var ok = TemperatureDayWindow.TryResolve(
            boundaryDate.ToString("yyyy-MM-dd"), FixedNowUtc, TimeZoneInfo.Utc, RetentionDays, out _, out _, out var error);

        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void TryResolve_OlderThanRetention_Fails()
    {
        var tooOld = DateOnly.FromDateTime(FixedNowUtc.DateTime).AddDays(-RetentionDays - 1);

        var ok = TemperatureDayWindow.TryResolve(
            tooOld.ToString("yyyy-MM-dd"), FixedNowUtc, TimeZoneInfo.Utc, RetentionDays, out _, out _, out var error);

        Assert.False(ok);
        Assert.Contains("retention", error);
    }
}
