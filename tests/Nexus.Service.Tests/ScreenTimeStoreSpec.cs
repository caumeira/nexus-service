using System;
using System.IO;
using Nexus.Service.Activity.Storage;
using Nexus.Service.Activity.Storage.Binary;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// The screen-time behavior IScreenTimeStore's methods must have -
/// local-date session bucketing (not a UTC day floor, so a session recorded
/// late at night lands on the wall-clock day the user saw), day/range/app/
/// hour aggregation, the QuerySessions overlap window, delete variants, and
/// case-sensitive app-name identity (unlike the metrics app-usage tier).
/// Runs against BinaryScreenTimeStore (BinaryScreenTimeStoreSpecTests).
/// </summary>
public abstract class ScreenTimeStoreSpec : IDisposable
{
    private readonly string _dir;
    protected IScreenTimeStore Store = null!;

    protected ScreenTimeStoreSpec()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-screentime-spec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        Store = CreateStore(_dir);
    }

    protected abstract IScreenTimeStore CreateStore(string dir);

    /// <summary>Disposes the current store and reopens a fresh instance at
    /// the same directory, simulating a service restart.</summary>
    protected void Reopen()
    {
        Store.Dispose();
        Store = CreateStore(_dir);
    }

    public virtual void Dispose()
    {
        Store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static long Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
    {
        var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
        return new DateTimeOffset(local).ToUnixTimeMilliseconds();
    }

    [Fact]
    public void RecordSession_WritesAndReadsBack()
    {
        var start = Utc(2026, 4, 20, 9, 0, 0);
        var end = start + 60_000;
        Store.RecordSession("Slack", null, start, end);

        var day = Store.GetDay(new DateOnly(2026, 4, 20));

        Assert.Equal("2026-04-20", day.Date);
        Assert.Equal(60_000, day.TotalMs);
        Assert.Equal(1, day.Pickups);
        Assert.Single(day.Apps);
        Assert.Equal("Slack", day.Apps[0].Name);
        Assert.Equal(60_000, day.Apps[0].TotalMs);
    }

    [Fact]
    public void GetDay_GroupsMultipleSessionsPerApp()
    {
        var baseUtc = Utc(2026, 4, 20, 9, 0, 0);
        Store.RecordSession("Code", null, baseUtc, baseUtc + 30_000);
        Store.RecordSession("Code", null, baseUtc + 60_000, baseUtc + 90_000);
        Store.RecordSession("Slack", null, baseUtc + 120_000, baseUtc + 150_000);

        var day = Store.GetDay(new DateOnly(2026, 4, 20));

        Assert.Equal(90_000, day.TotalMs);
        Assert.Equal(3, day.Pickups);
        Assert.Equal(2, day.Apps.Count);
        Assert.Equal("Code", day.Apps[0].Name);
        Assert.Equal(60_000, day.Apps[0].TotalMs);
        Assert.Equal("Slack", day.Apps[1].Name);
    }

    [Fact]
    public void GetDay_HourlyArrayHas24Entries()
    {
        Store.RecordSession("Edge", null, Utc(2026, 4, 20, 10, 0, 0), Utc(2026, 4, 20, 10, 5, 0));
        Store.RecordSession("Edge", null, Utc(2026, 4, 20, 14, 0, 0), Utc(2026, 4, 20, 14, 10, 0));

        var day = Store.GetDay(new DateOnly(2026, 4, 20));

        Assert.Equal(24, day.HourlyMs.Count);
        Assert.Equal(5 * 60 * 1000, day.HourlyMs[10]);
        Assert.Equal(10 * 60 * 1000, day.HourlyMs[14]);
        Assert.Equal(0, day.HourlyMs[0]);
        Assert.Equal(0, day.HourlyMs[23]);
    }

    [Fact]
    public void RecordSession_RejectsEmptyNameAndInverseRange()
    {
        Store.RecordSession("", null, Utc(2026, 4, 20, 9, 0, 0), Utc(2026, 4, 20, 9, 1, 0));
        Store.RecordSession("X", null, Utc(2026, 4, 20, 9, 1, 0), Utc(2026, 4, 20, 9, 0, 0));

        var day = Store.GetDay(new DateOnly(2026, 4, 20));
        Assert.Equal(0, day.TotalMs);
        Assert.Empty(day.Apps);
    }

    [Fact]
    public void SessionAcrossMidnight_BucketsToStartDay()
    {
        var start = Utc(2026, 4, 20, 23, 50, 0);
        var end = Utc(2026, 4, 21, 0, 30, 0);
        Store.RecordSession("Terminal", null, start, end);

        var d20 = Store.GetDay(new DateOnly(2026, 4, 20));
        var d21 = Store.GetDay(new DateOnly(2026, 4, 21));

        Assert.Equal(40 * 60 * 1000, d20.TotalMs);
        Assert.Equal(0, d21.TotalMs);
    }

    [Fact]
    public void GetRange_ReturnsOneRowPerDay()
    {
        Store.RecordSession("A", null, Utc(2026, 4, 18, 9, 0, 0), Utc(2026, 4, 18, 9, 10, 0));
        Store.RecordSession("B", null, Utc(2026, 4, 19, 9, 0, 0), Utc(2026, 4, 19, 9, 20, 0));
        Store.RecordSession("C", null, Utc(2026, 4, 20, 9, 0, 0), Utc(2026, 4, 20, 9, 30, 0));

        var range = Store.GetRange(new DateOnly(2026, 4, 18), new DateOnly(2026, 4, 20));

        Assert.Equal(3, range.Count);
        Assert.Equal("2026-04-18", range[0].Date);
        Assert.Equal(600_000, range[0].TotalMs);
        Assert.Equal("2026-04-19", range[1].Date);
        Assert.Equal(1_200_000, range[1].TotalMs);
        Assert.Equal("2026-04-20", range[2].Date);
        Assert.Equal(1_800_000, range[2].TotalMs);
    }

    [Fact]
    public void GetRange_ExcludesOutsideBounds()
    {
        Store.RecordSession("A", null, Utc(2026, 4, 17, 9, 0, 0), Utc(2026, 4, 17, 9, 10, 0));
        Store.RecordSession("B", null, Utc(2026, 4, 19, 9, 0, 0), Utc(2026, 4, 19, 9, 10, 0));
        Store.RecordSession("C", null, Utc(2026, 4, 21, 9, 0, 0), Utc(2026, 4, 21, 9, 10, 0));

        var range = Store.GetRange(new DateOnly(2026, 4, 18), new DateOnly(2026, 4, 20));

        Assert.Single(range);
        Assert.Equal("2026-04-19", range[0].Date);
    }

    [Fact]
    public void GetAppHistory_AggregatesOneAppAcrossDates()
    {
        Store.RecordSession("Slack", null, Utc(2026, 4, 18, 9, 0, 0), Utc(2026, 4, 18, 9, 5, 0));
        Store.RecordSession("Slack", null, Utc(2026, 4, 19, 10, 0, 0), Utc(2026, 4, 19, 10, 30, 0));
        Store.RecordSession("Code", null, Utc(2026, 4, 19, 11, 0, 0), Utc(2026, 4, 19, 11, 45, 0));

        var hist = Store.GetAppHistory("Slack", new DateOnly(2026, 4, 18), new DateOnly(2026, 4, 19));

        Assert.Equal("Slack", hist.AppName);
        Assert.Equal(2, hist.Daily.Count);
        Assert.Equal(2, hist.TotalPickups);
        Assert.Equal(35 * 60 * 1000, hist.TotalMs);
        Assert.Equal(30 * 60 * 1000, hist.LongestSessionMs);
    }

    [Fact]
    public void GetAppHistory_ReturnsAnEmptyHistory_ForAnAppNeverRecorded()
    {
        var hist = Store.GetAppHistory("never-seen", new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30));

        Assert.Equal("never-seen", hist.AppName);
        Assert.Empty(hist.Daily);
        Assert.Equal(0, hist.TotalMs);
        Assert.Equal(0, hist.TotalPickups);
        Assert.Equal(0, hist.LongestSessionMs);
    }

    [Fact]
    public void GetHourUsage_FiltersByHour()
    {
        Store.RecordSession("Slack", null, Utc(2026, 4, 20, 9, 0, 0), Utc(2026, 4, 20, 9, 30, 0));
        Store.RecordSession("Code", null, Utc(2026, 4, 20, 9, 30, 0), Utc(2026, 4, 20, 9, 45, 0));
        Store.RecordSession("Edge", null, Utc(2026, 4, 20, 10, 0, 0), Utc(2026, 4, 20, 10, 10, 0));

        var hour9 = Store.GetHourUsage(new DateOnly(2026, 4, 20), 9);

        Assert.Equal(2, hour9.Count);
        Assert.Equal("Slack", hour9[0].Name);
        Assert.Equal("Code", hour9[1].Name);
    }

    [Fact]
    public void DifferentlyCasedNames_AreTreatedAsDistinctApps()
    {
        var start = Utc(2026, 4, 20, 9, 0, 0);
        Store.RecordSession("Slack", null, start, start + 60_000);
        Store.RecordSession("slack", null, start + 120_000, start + 180_000);

        var day = Store.GetDay(new DateOnly(2026, 4, 20));

        Assert.Equal(2, day.Apps.Count);
        Assert.Equal(1, Store.GetAppHistory("Slack", new DateOnly(2026, 4, 20), new DateOnly(2026, 4, 20)).TotalPickups);
        Assert.Equal(1, Store.GetAppHistory("slack", new DateOnly(2026, 4, 20), new DateOnly(2026, 4, 20)).TotalPickups);
    }

    [Fact]
    public void QuerySessions_ReturnsSessionsOverlappingTheWindow_AscendingByStart()
    {
        var t0 = Utc(2026, 4, 20, 9, 0, 0);
        Store.RecordSession("A", null, t0, t0 + 60_000);
        Store.RecordSession("B", null, t0 + 120_000, t0 + 180_000);
        Store.RecordSession("C", null, t0 + 300_000, t0 + 360_000);

        var rows = Store.QuerySessions(t0 + 100_000, t0 + 200_000);

        var row = Assert.Single(rows);
        Assert.Equal("B", row.AppName);
    }

    [Fact]
    public void QuerySessions_FindsASessionThatStartedSeveralLocalDaysBefore_ButOverlapsTheWindow()
    {
        // A session's day file is keyed by its own start date; the window's
        // start date must not bound how far back that file walk looks, or a
        // long-running session recorded well before the window is missed
        // even though it still overlaps it.
        var start = Utc(2026, 4, 15, 8, 0, 0);
        var end = Utc(2026, 4, 25, 8, 0, 0);
        Store.RecordSession("LongRunning", null, start, end);

        var rows = Store.QuerySessions(Utc(2026, 4, 20, 0, 0, 0), Utc(2026, 4, 20, 1, 0, 0));

        var row = Assert.Single(rows);
        Assert.Equal("LongRunning", row.AppName);
    }

    [Fact]
    public void QuerySessions_FindsASessionThatStartedTheLocalDayBefore_ButOverlapsTheWindow()
    {
        var start = Utc(2026, 4, 20, 23, 50, 0);
        var end = Utc(2026, 4, 21, 0, 30, 0);
        Store.RecordSession("Terminal", null, start, end);

        var rows = Store.QuerySessions(Utc(2026, 4, 21, 0, 0, 0), Utc(2026, 4, 21, 1, 0, 0));

        var row = Assert.Single(rows);
        Assert.Equal("Terminal", row.AppName);
    }

    [Fact]
    public void DeleteDay_RemovesOnlyThatDay()
    {
        Store.RecordSession("A", null, Utc(2026, 4, 18, 9, 0, 0), Utc(2026, 4, 18, 9, 5, 0));
        Store.RecordSession("A", null, Utc(2026, 4, 19, 9, 0, 0), Utc(2026, 4, 19, 9, 5, 0));

        var deleted = Store.DeleteDay(new DateOnly(2026, 4, 18));

        Assert.Equal(1, deleted);
        Assert.Empty(Store.GetDay(new DateOnly(2026, 4, 18)).Apps);
        Assert.Single(Store.GetDay(new DateOnly(2026, 4, 19)).Apps);
    }

    [Fact]
    public void DeleteRange_IsInclusive()
    {
        Store.RecordSession("A", null, Utc(2026, 4, 17, 9, 0, 0), Utc(2026, 4, 17, 9, 5, 0));
        Store.RecordSession("A", null, Utc(2026, 4, 18, 9, 0, 0), Utc(2026, 4, 18, 9, 5, 0));
        Store.RecordSession("A", null, Utc(2026, 4, 19, 9, 0, 0), Utc(2026, 4, 19, 9, 5, 0));
        Store.RecordSession("A", null, Utc(2026, 4, 20, 9, 0, 0), Utc(2026, 4, 20, 9, 5, 0));

        var deleted = Store.DeleteRange(new DateOnly(2026, 4, 18), new DateOnly(2026, 4, 19));

        Assert.Equal(2, deleted);
        Assert.Equal(2, Store.GetRange(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30)).Count);
    }

    [Fact]
    public void DeleteApp_RemovesOneAppEverywhere()
    {
        Store.RecordSession("Slack", null, Utc(2026, 4, 18, 9, 0, 0), Utc(2026, 4, 18, 9, 5, 0));
        Store.RecordSession("Slack", null, Utc(2026, 4, 19, 9, 0, 0), Utc(2026, 4, 19, 9, 5, 0));
        Store.RecordSession("Code", null, Utc(2026, 4, 19, 9, 0, 0), Utc(2026, 4, 19, 9, 5, 0));

        var deleted = Store.DeleteApp("Slack");

        Assert.Equal(2, deleted);
        var d19 = Store.GetDay(new DateOnly(2026, 4, 19));
        Assert.Single(d19.Apps);
        Assert.Equal("Code", d19.Apps[0].Name);
    }

    [Fact]
    public void DeleteAll_EmptiesTheStore()
    {
        Store.RecordSession("A", null, Utc(2026, 4, 18, 9, 0, 0), Utc(2026, 4, 18, 9, 5, 0));
        Store.RecordSession("B", null, Utc(2026, 4, 19, 9, 0, 0), Utc(2026, 4, 19, 9, 5, 0));

        var deleted = Store.DeleteAll();

        Assert.Equal(2, deleted);
        Assert.Empty(Store.GetRange(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30)));
    }

    [Fact]
    public void StoreSurvivesReopen()
    {
        Store.RecordSession("Persisted", null, Utc(2026, 4, 20, 9, 0, 0), Utc(2026, 4, 20, 9, 30, 0));

        Reopen();

        var day = Store.GetDay(new DateOnly(2026, 4, 20));
        Assert.Single(day.Apps);
        Assert.Equal("Persisted", day.Apps[0].Name);
        Assert.Equal(30 * 60 * 1000, day.Apps[0].TotalMs);
    }
}

public sealed class BinaryScreenTimeStoreSpecTests : ScreenTimeStoreSpec
{
    protected override IScreenTimeStore CreateStore(string dir) =>
        new BinaryScreenTimeStore(dir);
}
