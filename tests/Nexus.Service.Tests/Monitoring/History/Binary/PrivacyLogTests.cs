using System;
using System.IO;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

/// <summary>
/// PrivacyLog mechanism-level tests that PrivacySessionHistorySpec's
/// store-agnostic behavior specs cannot express: RAM-index reconstruction
/// from the persisted log, torn-trailing-record recovery, and the
/// rewrite-on-prune compaction actually persisting to disk rather than only
/// updating the in-memory dictionary.
/// </summary>
public class PrivacyLogTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public PrivacyLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-privacylog-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "privacy.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Reopen_RebuildsTheIndex_FromThePersistedLog()
    {
        var log = new PrivacyLog(_path);
        log.Upsert("microphone", "app.exe", 1000, 1080);
        log.Upsert("webcam", "app2.exe", 2000, null);

        var reopened = new PrivacyLog(_path);

        var rows = reopened.Query(0, long.MaxValue);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.AppId == "app.exe" && r.Capability == "microphone" && r.EndUtcSec == 1080);
        Assert.Contains(rows, r => r.AppId == "app2.exe" && r.Capability == "webcam" && r.EndUtcSec == null);
    }

    [Fact]
    public void OpenSession_WithNullEnd_RoundTripsAcrossAReopen_WithoutLeakingTheSentinel()
    {
        var log = new PrivacyLog(_path);
        log.Upsert("location", "app.exe", 1000, null);

        var reopened = new PrivacyLog(_path);

        var row = Assert.Single(reopened.Query(0, long.MaxValue));
        Assert.Null(row.EndUtcSec);
    }

    [Fact]
    public void Reopen_WithATornTrailingRecord_KeepsEarlierSessions_AndTruncatesTheTornOne()
    {
        var log = new PrivacyLog(_path);
        log.Upsert("microphone", "app.exe", 1000, 1080);

        // Simulate a crash mid-append: a trailing record whose declared
        // appId length claims more bytes than actually follow it.
        const int claimedAppIdLen = 500;
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.End);
            fs.Write(BitConverter.GetBytes(claimedAppIdLen));
            fs.Write(new byte[] { 1, 2, 3 }); // far short of the claimed length
        }

        var reopened = new PrivacyLog(_path);

        var row = Assert.Single(reopened.Query(0, long.MaxValue));
        Assert.Equal("app.exe", row.AppId);

        // The torn tail was truncated away, so a fresh append lands right
        // after the last good record rather than behind unreachable
        // garbage - a further reopen must see both sessions.
        reopened.Upsert("webcam", "app2.exe", 2000, null);
        var afterAppend = new PrivacyLog(_path).Query(0, long.MaxValue);
        Assert.Equal(2, afterAppend.Count);
    }

    [Fact]
    public void PruneOlderThan_CompactsTheLogFile_SoAReopenOnlySeesSurvivors()
    {
        var log = new PrivacyLog(_path);
        log.Upsert("microphone", "app.exe", 1000, 1080);   // closed, old -> pruned
        log.Upsert("microphone", "app2.exe", 5000, 5080);  // closed, recent -> kept

        log.PruneOlderThan(2000);

        var reopened = new PrivacyLog(_path);
        var row = Assert.Single(reopened.Query(0, long.MaxValue));
        Assert.Equal("app2.exe", row.AppId);
    }
}
