using System;
using Nexus.Service.Activity.Storage;

namespace Nexus.Service.Tests;

public class InMemoryScreenTimeStoreTests
{
    private static long Utc(int year, int month, int day, int hour = 0, int minute = 0)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local);
        return new DateTimeOffset(local).ToUnixTimeMilliseconds();
    }

    [Fact]
    public void InMemoryStore_BehavesLikeSqlite_ForCoreOps()
    {
        using var store = new InMemoryScreenTimeStore();
        store.RecordSession("Slack", null, Utc(2026, 4, 20, 9), Utc(2026, 4, 20, 9, 30));
        store.RecordSession("Code", null, Utc(2026, 4, 20, 10), Utc(2026, 4, 20, 10, 45));
        store.RecordSession("Slack", null, Utc(2026, 4, 21, 9), Utc(2026, 4, 21, 9, 15));

        var day = store.GetDay(new DateOnly(2026, 4, 20));
        Assert.Equal(2, day.Apps.Count);
        Assert.Equal((long)(30 + 45) * 60 * 1000, day.TotalMs);

        var range = store.GetRange(new DateOnly(2026, 4, 19), new DateOnly(2026, 4, 22));
        Assert.Equal(2, range.Count);

        var hist = store.GetAppHistory("Slack", new DateOnly(2026, 4, 20), new DateOnly(2026, 4, 21));
        Assert.Equal(2, hist.TotalPickups);
        Assert.Equal(30 * 60 * 1000, hist.LongestSessionMs);

        Assert.Equal(1, store.DeleteApp("Code"));
        Assert.Single(store.GetDay(new DateOnly(2026, 4, 20)).Apps);
    }

    [Fact]
    public void QuerySessions_ReturnsOverlappingRowsOrderedByStart()
    {
        using var store = new InMemoryScreenTimeStore();
        store.RecordSession("Chrome", "/c", Utc(2026, 4, 20, 8), Utc(2026, 4, 20, 9));
        store.RecordSession("Code", "/v", Utc(2026, 4, 20, 10), Utc(2026, 4, 20, 11));
        store.RecordSession("Slack", null, Utc(2026, 4, 20, 14), Utc(2026, 4, 20, 15));

        // Window 8:30-10:30 overlaps the first two sessions (partial each), not Slack.
        var rows = store.QuerySessions(Utc(2026, 4, 20, 8, 30), Utc(2026, 4, 20, 10, 30));

        Assert.Equal(2, rows.Count);
        Assert.Equal("Chrome", rows[0].AppName);
        Assert.Equal("/c", rows[0].AppPath);
        Assert.Equal("Code", rows[1].AppName);
    }
}
