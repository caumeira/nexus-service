using System;
using System.IO;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// The privacy-session behavior IPrivacySessionStore.Upsert/Query/
/// PruneOlderThan must have - the upsert-keyed-by-(appId,capability,start)
/// open/close semantics, the query window that includes an open session
/// with no upper bound yet, and the closed-by-end/open-by-start prune
/// floor. Runs against BinaryMetricsHistoryStore
/// (BinaryPrivacySessionHistorySpecTests).
/// </summary>
public abstract class PrivacySessionHistorySpec : IDisposable
{
    private readonly string _dir;
    protected IPrivacySessionStore Store = null!;

    protected PrivacySessionHistorySpec()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-privacyhistory-spec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        Store = CreateStore(_dir);
    }

    protected abstract IPrivacySessionStore CreateStore(string dir);

    /// <summary>Disposes the current store and reopens a fresh instance at
    /// the same directory, simulating a service restart.</summary>
    protected void Reopen()
    {
        (Store as IDisposable)?.Dispose();
        Store = CreateStore(_dir);
    }

    public virtual void Dispose()
    {
        (Store as IDisposable)?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Upsert_ThenQuery_RoundTripsAnOpenSession()
    {
        Store.Upsert("microphone", "app.exe", 1000, null);

        var row = Assert.Single(Store.Query(0, 10_000));
        Assert.Equal("app.exe", row.AppId);
        Assert.Equal("microphone", row.Capability);
        Assert.Equal(1000, row.StartUtcSec);
        Assert.Null(row.EndUtcSec);
    }

    [Fact]
    public void Upsert_SameAppCapabilityStart_ClosesTheOpenSessionInPlace()
    {
        Store.Upsert("microphone", "app.exe", 1000, null);

        Store.Upsert("microphone", "app.exe", 1000, 1080);

        var row = Assert.Single(Store.Query(0, 10_000));
        Assert.Equal(1000, row.StartUtcSec);
        Assert.Equal(1080, row.EndUtcSec);
    }

    [Fact]
    public void Upsert_DifferentStart_IsASeparateSession()
    {
        Store.Upsert("microphone", "app.exe", 1000, 1080);
        Store.Upsert("microphone", "app.exe", 2000, null);

        var rows = Store.Query(0, 10_000);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Query_ExcludesSessionsOutsideTheWindow()
    {
        Store.Upsert("microphone", "app.exe", 1000, 1080);
        Store.Upsert("microphone", "app.exe", 5000, 5080);

        var rows = Store.Query(2000, 4000);

        Assert.Empty(rows);
    }

    [Fact]
    public void Query_IncludesAnOpenSession_EvenWhenItStartedBeforeTheWindow()
    {
        Store.Upsert("microphone", "app.exe", 100, null);

        var rows = Store.Query(5000, 10_000);

        var row = Assert.Single(rows);
        Assert.Null(row.EndUtcSec);
    }

    [Fact]
    public void Query_ExcludesAnOpenSession_WhenItStartsAfterTheWindow()
    {
        Store.Upsert("microphone", "app.exe", 20_000, null);

        var rows = Store.Query(0, 10_000);

        Assert.Empty(rows);
    }

    [Fact]
    public void Prune_DeletesClosedSessionsEndingBeforeTheCutoff()
    {
        Store.Upsert("microphone", "app.exe", 1000, 1080);   // closed, old -> pruned
        Store.Upsert("microphone", "app2.exe", 5000, 5080);  // closed, recent -> kept

        Store.PruneOlderThan(2000);

        var rows = Store.Query(0, long.MaxValue);
        var row = Assert.Single(rows);
        Assert.Equal("app2.exe", row.AppId);
    }

    // A backstop for a session that never got a proper close recorded
    // (PrivacyAccessTransitions handles the reachable orphan cases directly
    // - this test targets rows that predate that fix, or any future path it
    // doesn't cover).
    [Fact]
    public void Prune_DeletesAnOpenSessionWhoseStartPredatesTheCutoff()
    {
        Store.Upsert("microphone", "app.exe", 500, null);

        Store.PruneOlderThan(2000);

        Assert.Empty(Store.Query(0, long.MaxValue));
    }

    [Fact]
    public void Prune_KeepsAnOpenSessionWhoseStartIsWithinRetention()
    {
        Store.Upsert("microphone", "app.exe", 5000, null);

        Store.PruneOlderThan(2000);

        var row = Assert.Single(Store.Query(0, long.MaxValue));
        Assert.Null(row.EndUtcSec);
    }

    [Fact]
    public void Sessions_SurviveReopen()
    {
        Store.Upsert("webcam", "app.exe", 1000, 1080);

        Reopen();

        var row = Assert.Single(Store.Query(0, 10_000));
        Assert.Equal("webcam", row.Capability);
        Assert.Equal(1080, row.EndUtcSec);
    }
}

public sealed class BinaryPrivacySessionHistorySpecTests : PrivacySessionHistorySpec
{
    protected override IPrivacySessionStore CreateStore(string dir) =>
        new BinaryMetricsHistoryStore(dir);
}
