using System;
using System.IO;
using System.Linq;
using Nexus.Service.Activity.Storage;

namespace Nexus.Service.Tests;

public class SqliteScreenTimeStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly SqliteScreenTimeStore _store;

    public SqliteScreenTimeStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-screentime-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "screentime.db");
        _store = new SqliteScreenTimeStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
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
        _store.RecordSession("Slack", null, start, end);

        var day = _store.GetDay(new DateOnly(2026, 4, 20));

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
        _store.RecordSession("Code", null, baseUtc, baseUtc + 30_000);
        _store.RecordSession("Code", null, baseUtc + 60_000, baseUtc + 90_000);
        _store.RecordSession("Slack", null, baseUtc + 120_000, baseUtc + 150_000);

        var day = _store.GetDay(new DateOnly(2026, 4, 20));

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
        _store.RecordSession("Edge", null, Utc(2026, 4, 20, 10, 0, 0), Utc(2026, 4, 20, 10, 5, 0));
        _store.RecordSession("Edge", null, Utc(2026, 4, 20, 14, 0, 0), Utc(2026, 4, 20, 14, 10, 0));

        var day = _store.GetDay(new DateOnly(2026, 4, 20));

        Assert.Equal(24, day.HourlyMs.Count);
        Assert.Equal(5 * 60 * 1000, day.HourlyMs[10]);
        Assert.Equal(10 * 60 * 1000, day.HourlyMs[14]);
        Assert.Equal(0, day.HourlyMs[0]);
        Assert.Equal(0, day.HourlyMs[23]);
    }

    [Fact]
    public void RecordSession_RejectsEmptyNameAndInverseRange()
    {
        _store.RecordSession("", null, Utc(2026, 4, 20, 9, 0, 0), Utc(2026, 4, 20, 9, 1, 0));
        _store.RecordSession("X", null, Utc(2026, 4, 20, 9, 1, 0), Utc(2026, 4, 20, 9, 0, 0));

        var day = _store.GetDay(new DateOnly(2026, 4, 20));
        Assert.Equal(0, day.TotalMs);
        Assert.Empty(day.Apps);
    }

    [Fact]
    public void SessionAcrossMidnight_BucketsToStartDay()
    {
        var start = Utc(2026, 4, 20, 23, 50, 0);
        var end = Utc(2026, 4, 21, 0, 30, 0);
        _store.RecordSession("Terminal", null, start, end);

        var d20 = _store.GetDay(new DateOnly(2026, 4, 20));
        var d21 = _store.GetDay(new DateOnly(2026, 4, 21));

        Assert.Equal(40 * 60 * 1000, d20.TotalMs);
        Assert.Equal(0, d21.TotalMs);
    }

    [Fact]
    public void GetRange_ReturnsOneRowPerDay()
    {
        _store.RecordSession("A", null, Utc(2026, 4, 18, 9, 0, 0), Utc(2026, 4, 18, 9, 10, 0));
        _store.RecordSession("B", null, Utc(2026, 4, 19, 9, 0, 0), Utc(2026, 4, 19, 9, 20, 0));
        _store.RecordSession("C", null, Utc(2026, 4, 20, 9, 0, 0), Utc(2026, 4, 20, 9, 30, 0));

        var range = _store.GetRange(new DateOnly(2026, 4, 18), new DateOnly(2026, 4, 20));

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
        _store.RecordSession("A", null, Utc(2026, 4, 17, 9, 0, 0), Utc(2026, 4, 17, 9, 10, 0));
        _store.RecordSession("B", null, Utc(2026, 4, 19, 9, 0, 0), Utc(2026, 4, 19, 9, 10, 0));
        _store.RecordSession("C", null, Utc(2026, 4, 21, 9, 0, 0), Utc(2026, 4, 21, 9, 10, 0));

        var range = _store.GetRange(new DateOnly(2026, 4, 18), new DateOnly(2026, 4, 20));

        Assert.Single(range);
        Assert.Equal("2026-04-19", range[0].Date);
    }

    [Fact]
    public void GetAppHistory_AggregatesOneAppAcrossDates()
    {
        _store.RecordSession("Slack", null, Utc(2026, 4, 18, 9, 0, 0), Utc(2026, 4, 18, 9, 5, 0));
        _store.RecordSession("Slack", null, Utc(2026, 4, 19, 10, 0, 0), Utc(2026, 4, 19, 10, 30, 0));
        _store.RecordSession("Code", null, Utc(2026, 4, 19, 11, 0, 0), Utc(2026, 4, 19, 11, 45, 0));

        var hist = _store.GetAppHistory("Slack", new DateOnly(2026, 4, 18), new DateOnly(2026, 4, 19));

        Assert.Equal("Slack", hist.AppName);
        Assert.Equal(2, hist.Daily.Count);
        Assert.Equal(2, hist.TotalPickups);
        Assert.Equal(35 * 60 * 1000, hist.TotalMs);
        Assert.Equal(30 * 60 * 1000, hist.LongestSessionMs);
    }

    [Fact]
    public void GetHourUsage_FiltersByHour()
    {
        _store.RecordSession("Slack", null, Utc(2026, 4, 20, 9, 0, 0), Utc(2026, 4, 20, 9, 30, 0));
        _store.RecordSession("Code", null, Utc(2026, 4, 20, 9, 30, 0), Utc(2026, 4, 20, 9, 45, 0));
        _store.RecordSession("Edge", null, Utc(2026, 4, 20, 10, 0, 0), Utc(2026, 4, 20, 10, 10, 0));

        var hour9 = _store.GetHourUsage(new DateOnly(2026, 4, 20), 9);

        Assert.Equal(2, hour9.Count);
        Assert.Equal("Slack", hour9[0].Name);
        Assert.Equal("Code", hour9[1].Name);
    }

    [Fact]
    public void DeleteDay_RemovesOnlyThatDay()
    {
        _store.RecordSession("A", null, Utc(2026, 4, 18, 9, 0, 0), Utc(2026, 4, 18, 9, 5, 0));
        _store.RecordSession("A", null, Utc(2026, 4, 19, 9, 0, 0), Utc(2026, 4, 19, 9, 5, 0));

        var deleted = _store.DeleteDay(new DateOnly(2026, 4, 18));

        Assert.Equal(1, deleted);
        Assert.Empty(_store.GetDay(new DateOnly(2026, 4, 18)).Apps);
        Assert.Single(_store.GetDay(new DateOnly(2026, 4, 19)).Apps);
    }

    [Fact]
    public void DeleteRange_IsInclusive()
    {
        _store.RecordSession("A", null, Utc(2026, 4, 17, 9, 0, 0), Utc(2026, 4, 17, 9, 5, 0));
        _store.RecordSession("A", null, Utc(2026, 4, 18, 9, 0, 0), Utc(2026, 4, 18, 9, 5, 0));
        _store.RecordSession("A", null, Utc(2026, 4, 19, 9, 0, 0), Utc(2026, 4, 19, 9, 5, 0));
        _store.RecordSession("A", null, Utc(2026, 4, 20, 9, 0, 0), Utc(2026, 4, 20, 9, 5, 0));

        var deleted = _store.DeleteRange(new DateOnly(2026, 4, 18), new DateOnly(2026, 4, 19));

        Assert.Equal(2, deleted);
        Assert.Equal(2, _store.GetRange(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30)).Count);
    }

    [Fact]
    public void DeleteApp_RemovesOneAppEverywhere()
    {
        _store.RecordSession("Slack", null, Utc(2026, 4, 18, 9, 0, 0), Utc(2026, 4, 18, 9, 5, 0));
        _store.RecordSession("Slack", null, Utc(2026, 4, 19, 9, 0, 0), Utc(2026, 4, 19, 9, 5, 0));
        _store.RecordSession("Code", null, Utc(2026, 4, 19, 9, 0, 0), Utc(2026, 4, 19, 9, 5, 0));

        var deleted = _store.DeleteApp("Slack");

        Assert.Equal(2, deleted);
        var d19 = _store.GetDay(new DateOnly(2026, 4, 19));
        Assert.Single(d19.Apps);
        Assert.Equal("Code", d19.Apps[0].Name);
    }

    [Fact]
    public void DeleteAll_EmptiesTheTable()
    {
        _store.RecordSession("A", null, Utc(2026, 4, 18, 9, 0, 0), Utc(2026, 4, 18, 9, 5, 0));
        _store.RecordSession("B", null, Utc(2026, 4, 19, 9, 0, 0), Utc(2026, 4, 19, 9, 5, 0));

        var deleted = _store.DeleteAll();

        Assert.Equal(2, deleted);
        Assert.Empty(_store.GetRange(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30)));
    }

    [Fact]
    public void StoreSurvivesReopen()
    {
        _store.RecordSession("Persisted", null, Utc(2026, 4, 20, 9, 0, 0), Utc(2026, 4, 20, 9, 30, 0));
        _store.Dispose();

        using var reopened = new SqliteScreenTimeStore(_dbPath);
        var day = reopened.GetDay(new DateOnly(2026, 4, 20));

        Assert.Single(day.Apps);
        Assert.Equal("Persisted", day.Apps[0].Name);
        Assert.Equal(30 * 60 * 1000, day.Apps[0].TotalMs);
    }
}
