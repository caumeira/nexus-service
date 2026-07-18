using System;
using System.IO;
using Nexus.Service.Activity.Storage.Binary;
using Xunit;

namespace Nexus.Service.Tests.Activity.Storage.Binary;

/// <summary>
/// BinaryScreenTimeStore mechanism-level tests that ScreenTimeStoreSpec's
/// store-agnostic behavior specs cannot express: day-segment files named by
/// local calendar date, torn-trailing-record recovery (a crash mid-append),
/// and DeleteApp's scan-and-rewrite actually compacting the file on disk
/// rather than only updating what a single still-open instance reports.
/// </summary>
public class BinaryScreenTimeStoreTests : IDisposable
{
    private readonly string _dir;

    public BinaryScreenTimeStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-binaryscreentime-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static long Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
    {
        var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
        return new DateTimeOffset(local).ToUnixTimeMilliseconds();
    }

    [Fact]
    public void RecordSession_OnDifferentLocalDates_WritesToSeparateDaySegmentFiles()
    {
        using var store = new BinaryScreenTimeStore(_dir);

        store.RecordSession("app.exe", null, Utc(2026, 4, 20, 9, 0, 0), Utc(2026, 4, 20, 9, 1, 0));
        store.RecordSession("app.exe", null, Utc(2026, 4, 21, 9, 0, 0), Utc(2026, 4, 21, 9, 1, 0));

        Assert.True(File.Exists(Path.Combine(_dir, "2026-04-20.seg")));
        Assert.True(File.Exists(Path.Combine(_dir, "2026-04-21.seg")));
    }

    [Fact]
    public void RecordSession_WithATornTrailingRecord_TruncatesIt_SoALaterSessionLandsCleanly()
    {
        using var store = new BinaryScreenTimeStore(_dir);
        store.RecordSession("app.exe", null, Utc(2026, 4, 20, 9, 0, 0), Utc(2026, 4, 20, 9, 1, 0));

        // Simulate a crash mid-append: a partial trailing record.
        var segPath = Path.Combine(_dir, "2026-04-20.seg");
        using (var fs = new FileStream(segPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.End);
            fs.Write(new byte[] { 1, 2, 3, 4, 5 });
        }

        store.RecordSession("app.exe", null, Utc(2026, 4, 20, 10, 0, 0), Utc(2026, 4, 20, 10, 2, 0));

        var day = store.GetDay(new DateOnly(2026, 4, 20));
        Assert.Equal(2, day.Pickups);
        Assert.Equal((60 + 120) * 1000, day.TotalMs);
    }

    [Fact]
    public void DeleteApp_RewritesTheFile_SoAReopenOnlySeesSurvivors()
    {
        using (var store = new BinaryScreenTimeStore(_dir))
        {
            store.RecordSession("Slack", null, Utc(2026, 4, 20, 9, 0, 0), Utc(2026, 4, 20, 9, 5, 0));
            store.RecordSession("Code", null, Utc(2026, 4, 20, 10, 0, 0), Utc(2026, 4, 20, 10, 5, 0));

            var deleted = store.DeleteApp("Slack");
            Assert.Equal(1, deleted);
        }

        using var reopened = new BinaryScreenTimeStore(_dir);
        var day = reopened.GetDay(new DateOnly(2026, 4, 20));

        var app = Assert.Single(day.Apps);
        Assert.Equal("Code", app.Name);
    }
}
