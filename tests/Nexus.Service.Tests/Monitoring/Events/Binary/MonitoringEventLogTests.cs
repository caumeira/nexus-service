using System;
using System.IO;
using Nexus.Service.Monitoring.Events;
using Nexus.Service.Monitoring.Events.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.Events.Binary;

/// <summary>
/// MonitoringEventLog mechanism-level tests: append/query ordering, the
/// limit-keeps-newest contract, custom-only delete, prune compaction, and
/// crash recovery - the same coverage shape as PrivacyLogTests for its
/// sibling append-only log.
/// </summary>
public class MonitoringEventLogTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public MonitoringEventLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-eventlog-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "events.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Append_AssignsMonotonicIds_StartingAtOne()
    {
        var log = new MonitoringEventLog(_path);

        var first = log.Append(1000, MonitoringEventKinds.AppOpen, "app.exe", "/path/app.exe", custom: false);
        var second = log.Append(2000, MonitoringEventKinds.UsbAttach, "Mouse", "046D:C08B", custom: false);

        Assert.Equal(1, first.Id);
        Assert.Equal(2, second.Id);
    }

    [Fact]
    public void Query_ReturnsMatches_OrderedByTimeAscending_RegardlessOfAppendOrder()
    {
        var log = new MonitoringEventLog(_path);
        log.Append(3000, MonitoringEventKinds.AppOpen, "third", null, custom: false);
        log.Append(1000, MonitoringEventKinds.AppOpen, "first", null, custom: false);
        log.Append(2000, MonitoringEventKinds.AppOpen, "second", null, custom: false);

        var rows = log.Query(0, 10_000, 500);

        Assert.Equal(3, rows.Count);
        Assert.Equal("first", rows[0].Label);
        Assert.Equal("second", rows[1].Label);
        Assert.Equal("third", rows[2].Label);
    }

    [Fact]
    public void Query_ExcludesEventsOutsideTheWindow()
    {
        var log = new MonitoringEventLog(_path);
        log.Append(500, MonitoringEventKinds.AppOpen, "too-early", null, custom: false);
        log.Append(1500, MonitoringEventKinds.AppOpen, "in-window", null, custom: false);
        log.Append(5000, MonitoringEventKinds.AppOpen, "too-late", null, custom: false);

        var rows = log.Query(1000, 2000, 500);

        var row = Assert.Single(rows);
        Assert.Equal("in-window", row.Label);
    }

    [Fact]
    public void Query_WhenMoreMatchesThanLimit_KeepsTheNewestOnes_StillAscending()
    {
        var log = new MonitoringEventLog(_path);
        for (var i = 0; i < 5; i++)
        {
            log.Append(1000 + i, MonitoringEventKinds.UsbAttach, $"device-{i}", null, custom: false);
        }

        var rows = log.Query(0, 10_000, 2);

        Assert.Equal(2, rows.Count);
        Assert.Equal("device-3", rows[0].Label);
        Assert.Equal("device-4", rows[1].Label);
    }

    [Fact]
    public void Append_RoundTripsANullDetail()
    {
        var log = new MonitoringEventLog(_path);
        log.Append(1000, MonitoringEventKinds.UsbDetach, "device", null, custom: false);

        var row = Assert.Single(log.Query(0, 10_000, 500));
        Assert.Null(row.Detail);
    }

    [Fact]
    public void DeleteCustom_RemovesACustomEvent_AndReturnsTrue()
    {
        var log = new MonitoringEventLog(_path);
        var created = log.Append(1000, MonitoringEventKinds.Custom, "my note", null, custom: true);

        var deleted = log.DeleteCustom(created.Id);

        Assert.True(deleted);
        Assert.Empty(log.Query(0, 10_000, 500));
    }

    [Fact]
    public void DeleteCustom_RejectsANonCustomEvent_AndLeavesItInPlace()
    {
        var log = new MonitoringEventLog(_path);
        var created = log.Append(1000, MonitoringEventKinds.AppOpen, "app.exe", null, custom: false);

        var deleted = log.DeleteCustom(created.Id);

        Assert.False(deleted);
        Assert.Single(log.Query(0, 10_000, 500));
    }

    [Fact]
    public void DeleteCustom_WithAnUnknownId_ReturnsFalse()
    {
        var log = new MonitoringEventLog(_path);
        log.Append(1000, MonitoringEventKinds.Custom, "note", null, custom: true);

        var deleted = log.DeleteCustom(999);

        Assert.False(deleted);
    }

    [Fact]
    public void PruneOlderThan_DropsOldEvents_AndCompactsTheFile_SoAReopenOnlySeesSurvivors()
    {
        var log = new MonitoringEventLog(_path);
        log.Append(1000, MonitoringEventKinds.AppOpen, "old", null, custom: false);
        log.Append(5000, MonitoringEventKinds.AppOpen, "recent", null, custom: false);

        log.PruneOlderThan(2000);

        var reopened = new MonitoringEventLog(_path);
        var row = Assert.Single(reopened.Query(0, 10_000, 500));
        Assert.Equal("recent", row.Label);
    }

    [Fact]
    public void Reopen_RebuildsTheInRamList_FromThePersistedLog()
    {
        var log = new MonitoringEventLog(_path);
        log.Append(1000, MonitoringEventKinds.AppOpen, "app.exe", "/bin/app.exe", custom: false);
        log.Append(2000, MonitoringEventKinds.Custom, "note", null, custom: true);

        var reopened = new MonitoringEventLog(_path);

        var rows = reopened.Query(0, 10_000, 500);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Kind == MonitoringEventKinds.AppOpen && r.Detail == "/bin/app.exe");
        Assert.Contains(rows, r => r.Kind == MonitoringEventKinds.Custom && r.Custom);
    }

    [Fact]
    public void Reopen_ResumesIdsPastTheHighestSeen()
    {
        var log = new MonitoringEventLog(_path);
        log.Append(1000, MonitoringEventKinds.AppOpen, "first", null, custom: false);
        log.Append(2000, MonitoringEventKinds.AppOpen, "second", null, custom: false);

        var reopened = new MonitoringEventLog(_path);
        var third = reopened.Append(3000, MonitoringEventKinds.AppOpen, "third", null, custom: false);

        Assert.Equal(3, third.Id);
    }

    [Fact]
    public void Reopen_WithATornTrailingRecord_KeepsEarlierEvents_AndTruncatesTheTornOne()
    {
        var log = new MonitoringEventLog(_path);
        log.Append(1000, MonitoringEventKinds.AppOpen, "app.exe", null, custom: false);

        // Simulate a crash mid-append: a trailing record whose declared kind
        // length claims more bytes than actually follow it.
        const int claimedKindLen = 500;
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.End);
            fs.Write(BitConverter.GetBytes(9999L));   // id
            fs.Write(BitConverter.GetBytes(9000L));   // tUtcMs
            fs.Write(BitConverter.GetBytes(claimedKindLen));
            fs.Write(new byte[] { 1, 2, 3 });          // far short of the claimed length
        }

        var reopened = new MonitoringEventLog(_path);

        var row = Assert.Single(reopened.Query(0, 10_000, 500));
        Assert.Equal("app.exe", row.Label);

        // The torn tail was truncated away, so a fresh append lands right
        // after the last good record rather than behind unreachable
        // garbage - a further reopen must see both events.
        reopened.Append(2000, MonitoringEventKinds.UsbAttach, "device", null, custom: false);
        var afterAppend = new MonitoringEventLog(_path).Query(0, 10_000, 500);
        Assert.Equal(2, afterAppend.Count);
    }
}
