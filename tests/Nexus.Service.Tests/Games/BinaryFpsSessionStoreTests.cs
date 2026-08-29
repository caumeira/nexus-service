using Nexus.Service.Games;

namespace Nexus.Service.Tests.Games;

public class BinaryFpsSessionStoreTests : IDisposable
{
    private readonly string _dir;

    public BinaryFpsSessionStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-fpsstore-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Recent, not a fixed historical constant: the store prunes month
    // segments wholly outside its 1-year retention window on every Append,
    // so a fixture timestamped years in the past is deleted the instant it
    // is written.
    private static readonly long NowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static readonly long PreviousMonthUtcMs = DateTimeOffset.UtcNow.AddMonths(-1).ToUnixTimeMilliseconds();

    private static FpsSessionRecord Session(
        string gameKey = "steam:1091500", string name = "Cyberpunk 2077", string store = "steam",
        long? startedUtcMs = null, long? endedUtcMs = null,
        int focusedSec = 600, int validSec = 590, long frames = 590 * 60,
        int minFps = 30, int maxFps = 144, uint[]? hist = null,
        int dispW = 2560, int dispH = 1440, int refreshHz = 144, int winW = 2560, int winH = 1440,
        bool fullscreen = true, bool capped = false, int capValue = 0, ulong hardwareHash = 12345)
    {
        var started = startedUtcMs ?? NowUtcMs;
        var ended = endedUtcMs ?? started + 600_000;
        var h = hist ?? new uint[FpsHistogram.BucketCount];
        if (hist is null)
        {
            FpsHistogram.AddSample(h, 60);
        }
        return new FpsSessionRecord(
            Guid.NewGuid(), gameKey, name, store, started, ended, focusedSec, validSec, frames,
            minFps, maxFps, h, dispW, dispH, refreshHz, winW, winH, fullscreen, capped, capValue, hardwareHash,
            FpsUploadState.Pending);
    }

    [Fact]
    public void Append_ThenQuerySessions_RoundTripsEveryField()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        var record = Session();
        store.Append(record);

        var sessions = store.QuerySessions("steam:1091500", 10);

        var row = Assert.Single(sessions);
        Assert.Equal(record.Id, row.Id);
        Assert.Equal(record.GameKey, row.GameKey);
        Assert.Equal(record.GameName, row.GameName);
        Assert.Equal(record.Store, row.Store);
        Assert.Equal(record.StartedUtcMs, row.StartedUtcMs);
        Assert.Equal(record.EndedUtcMs, row.EndedUtcMs);
        Assert.Equal(record.FocusedSec, row.FocusedSec);
        Assert.Equal(record.ValidSec, row.ValidSec);
        Assert.Equal(record.Frames, row.Frames);
        Assert.Equal(record.MinFps, row.MinFps);
        Assert.Equal(record.MaxFps, row.MaxFps);
        Assert.Equal(record.Hist, row.Hist);
        Assert.Equal(record.DispW, row.DispW);
        Assert.Equal(record.DispH, row.DispH);
        Assert.Equal(record.RefreshHz, row.RefreshHz);
        Assert.Equal(record.WinW, row.WinW);
        Assert.Equal(record.WinH, row.WinH);
        Assert.Equal(record.Fullscreen, row.Fullscreen);
        Assert.Equal(record.Capped, row.Capped);
        Assert.Equal(record.HardwareHash, row.HardwareHash);
    }

    [Fact]
    public void QuerySessions_UnknownGameKey_ReturnsEmpty()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        store.Append(Session());

        Assert.Empty(store.QuerySessions("steam:999", 10));
    }

    [Fact]
    public void QuerySessions_ReturnsNewestFirst()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        var older = Session(startedUtcMs: NowUtcMs, endedUtcMs: NowUtcMs + 100_000);
        var newer = Session(startedUtcMs: NowUtcMs + 1_000_000, endedUtcMs: NowUtcMs + 1_100_000);
        store.Append(older);
        store.Append(newer);

        var sessions = store.QuerySessions("steam:1091500", 10);

        Assert.Equal(2, sessions.Count);
        Assert.Equal(newer.Id, sessions[0].Id);
        Assert.Equal(older.Id, sessions[1].Id);
    }

    [Fact]
    public void QuerySessions_RespectsLimit()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        for (var i = 0; i < 5; i++)
        {
            store.Append(Session(startedUtcMs: NowUtcMs + i * 1000, endedUtcMs: NowUtcMs + i * 1000 + 600_000));
        }

        Assert.Equal(2, store.QuerySessions("steam:1091500", 2).Count);
    }

    [Fact]
    public void QueryGameSummaries_MergesAcrossSessions()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        store.Append(Session(focusedSec: 600, validSec: 590, frames: 590 * 60, minFps: 40, maxFps: 100,
            startedUtcMs: NowUtcMs, endedUtcMs: NowUtcMs + 600_000));
        var lastPlayedUtcMs = NowUtcMs + 1_400_000;
        store.Append(Session(focusedSec: 400, validSec: 390, frames: 390 * 80, minFps: 20, maxFps: 144,
            startedUtcMs: NowUtcMs + 1_000_000, endedUtcMs: lastPlayedUtcMs));

        var summaries = store.QueryGameSummaries();

        var summary = Assert.Single(summaries);
        Assert.Equal("steam:1091500", summary.GameKey);
        Assert.Equal("1091500", summary.SteamAppId);
        Assert.Equal(2, summary.Sessions);
        Assert.Equal(1000, summary.FocusedSec);
        Assert.Equal(980, summary.ValidSec);
        Assert.Equal(20, summary.MinFps);
        Assert.Equal(144, summary.MaxFps);
        Assert.Equal(lastPlayedUtcMs, summary.LastPlayedUtcMs);
    }

    [Fact]
    public void QueryGameSummaries_NonSteamGame_HasNoSteamAppId()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        store.Append(Session(gameKey: "epic:fortnite", name: "Fortnite", store: "epic"));

        var summary = Assert.Single(store.QueryGameSummaries());

        Assert.Null(summary.SteamAppId);
    }

    [Fact]
    public void DeleteAll_RemovesEverySession_AndReportsTheCount()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        store.Append(Session());
        store.Append(Session(startedUtcMs: NowUtcMs + 1_000_000, endedUtcMs: NowUtcMs + 1_600_000));

        var deleted = store.DeleteAll();

        Assert.Equal(2, deleted);
        Assert.Empty(store.QuerySessions("steam:1091500", 10));
        Assert.Empty(store.QueryGameSummaries());
    }

    [Fact]
    public void DeleteSession_OneOfThreeInAMonth_KeepsTheOtherTwoReadableAfterReopen()
    {
        FpsSessionRecord first, second, third;
        using (var store = new BinaryFpsSessionStore(_dir))
        {
            first = Session(startedUtcMs: NowUtcMs, endedUtcMs: NowUtcMs + 600_000);
            second = Session(startedUtcMs: NowUtcMs + 1_000_000, endedUtcMs: NowUtcMs + 1_600_000);
            third = Session(startedUtcMs: NowUtcMs + 2_000_000, endedUtcMs: NowUtcMs + 2_600_000);
            store.Append(first);
            store.Append(second);
            store.Append(third);

            Assert.Equal(1, store.DeleteSession(second.Id));
        }

        using var reopened = new BinaryFpsSessionStore(_dir);
        var remaining = reopened.QuerySessions("steam:1091500", 10);

        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, r => r.Id == first.Id);
        Assert.Contains(remaining, r => r.Id == third.Id);
        Assert.DoesNotContain(remaining, r => r.Id == second.Id);
    }

    [Fact]
    public void DeleteSession_UnknownId_ReturnsZero_AndLeavesExistingSessionsIntact()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        store.Append(Session());

        var deleted = store.DeleteSession(Guid.NewGuid());

        Assert.Equal(0, deleted);
        Assert.Single(store.QuerySessions("steam:1091500", 10));
    }

    [Fact]
    public void DeleteGame_AcrossTwoMonths_RemovesEverySessionForThatGameOnly()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        store.Append(Session(startedUtcMs: NowUtcMs, endedUtcMs: NowUtcMs + 600_000));
        store.Append(Session(startedUtcMs: PreviousMonthUtcMs, endedUtcMs: PreviousMonthUtcMs + 600_000));
        store.Append(Session(gameKey: "epic:otherb", name: "Other Game", store: "epic",
            startedUtcMs: NowUtcMs, endedUtcMs: NowUtcMs + 600_000));

        var deleted = store.DeleteGame("steam:1091500");

        Assert.Equal(2, deleted);
        Assert.Empty(store.QuerySessions("steam:1091500", 10));
        Assert.Single(store.QuerySessions("epic:otherb", 10));
    }

    [Fact]
    public void DeleteGame_UnknownGameKey_ReturnsZero()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        store.Append(Session());

        var deleted = store.DeleteGame("steam:does-not-exist");

        Assert.Equal(0, deleted);
        Assert.Single(store.QuerySessions("steam:1091500", 10));
    }

    [Fact]
    public void Append_RejectsAHistogramOfTheWrongLength()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        var badRecord = Session(hist: new uint[10]);

        Assert.Throws<ArgumentException>(() => store.Append(badRecord));
    }

    [Fact]
    public void QuerySessionsByTimeRange_ReturnsSessionsAcrossGames_NewestFirst()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        var older = Session(gameKey: "steam:1", name: "Game A", startedUtcMs: NowUtcMs, endedUtcMs: NowUtcMs + 600_000);
        var newer = Session(gameKey: "epic:gameb", name: "Game B", store: "epic",
            startedUtcMs: NowUtcMs + 1_000_000, endedUtcMs: NowUtcMs + 1_600_000);
        store.Append(older);
        store.Append(newer);

        var sessions = store.QuerySessionsByTimeRange(NowUtcMs, NowUtcMs + 2_000_000, limit: 10);

        Assert.Equal(2, sessions.Count);
        Assert.Equal(newer.Id, sessions[0].Id);
        Assert.Equal(older.Id, sessions[1].Id);
    }

    [Fact]
    public void QuerySessionsByTimeRange_ExcludesSessionsOutsideTheRange()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        store.Append(Session(startedUtcMs: NowUtcMs, endedUtcMs: NowUtcMs + 600_000));

        var sessions = store.QuerySessionsByTimeRange(NowUtcMs + 10_000_000, NowUtcMs + 20_000_000, limit: 10);

        Assert.Empty(sessions);
    }

    [Fact]
    public void QuerySessionsByTimeRange_ASessionOverlappingTheRangeBoundary_IsIncluded()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        var session = Session(startedUtcMs: NowUtcMs, endedUtcMs: NowUtcMs + 600_000);
        store.Append(session);

        // The query window starts mid-session and ends after it.
        var sessions = store.QuerySessionsByTimeRange(NowUtcMs + 300_000, NowUtcMs + 900_000, limit: 10);

        Assert.Single(sessions);
    }

    [Fact]
    public void QuerySessionsByTimeRange_RespectsLimit()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        for (var i = 0; i < 5; i++)
        {
            store.Append(Session(startedUtcMs: NowUtcMs + i * 1_000, endedUtcMs: NowUtcMs + i * 1_000 + 600_000));
        }

        var sessions = store.QuerySessionsByTimeRange(NowUtcMs, NowUtcMs + 1_000_000, limit: 2);

        Assert.Equal(2, sessions.Count);
    }

    private static long UtcMsForLocalDate(DateOnly localDate, int hour = 12)
    {
        var local = new DateTime(localDate.Year, localDate.Month, localDate.Day, hour, 0, 0, DateTimeKind.Local);
        return new DateTimeOffset(local).ToUnixTimeMilliseconds();
    }

    [Fact]
    public void DeleteRange_MidRangeDay_KeepsEarlierAndLaterSessionsReadableAfterReopen()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var yesterday = today.AddDays(-1);
        var tomorrow = today.AddDays(1);

        FpsSessionRecord earlier, middle, later;
        using (var store = new BinaryFpsSessionStore(_dir))
        {
            earlier = Session(startedUtcMs: UtcMsForLocalDate(yesterday), endedUtcMs: UtcMsForLocalDate(yesterday) + 600_000);
            middle = Session(startedUtcMs: UtcMsForLocalDate(today), endedUtcMs: UtcMsForLocalDate(today) + 600_000);
            later = Session(startedUtcMs: UtcMsForLocalDate(tomorrow), endedUtcMs: UtcMsForLocalDate(tomorrow) + 600_000);
            store.Append(earlier);
            store.Append(middle);
            store.Append(later);

            Assert.Equal(1, store.DeleteRange(today, today));
        }

        using var reopened = new BinaryFpsSessionStore(_dir);
        var remaining = reopened.QuerySessions("steam:1091500", 10);

        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, r => r.Id == earlier.Id);
        Assert.Contains(remaining, r => r.Id == later.Id);
        Assert.DoesNotContain(remaining, r => r.Id == middle.Id);
    }

    [Fact]
    public void DeleteRange_SpanningTwoMonths_RemovesSessionsInBothMonths()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        var thisMonth = Session(startedUtcMs: NowUtcMs, endedUtcMs: NowUtcMs + 600_000);
        var lastMonth = Session(startedUtcMs: PreviousMonthUtcMs, endedUtcMs: PreviousMonthUtcMs + 600_000);
        store.Append(thisMonth);
        store.Append(lastMonth);

        var from = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(PreviousMonthUtcMs).ToLocalTime().Date).AddDays(-1);
        var to = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(NowUtcMs).ToLocalTime().Date).AddDays(1);

        var deleted = store.DeleteRange(from, to);

        Assert.Equal(2, deleted);
        Assert.Empty(store.QuerySessions("steam:1091500", 10));
    }

    [Fact]
    public void DeleteRange_FromAfterTo_ReturnsZero()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        store.Append(Session());

        var today = DateOnly.FromDateTime(DateTime.Now);
        var deleted = store.DeleteRange(today, today.AddDays(-1));

        Assert.Equal(0, deleted);
        Assert.Single(store.QuerySessions("steam:1091500", 10));
    }

    [Fact]
    public void DeleteRange_NoSessionsInRange_ReturnsZero()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        store.Append(Session());

        var farFuture = DateOnly.FromDateTime(DateTime.Now).AddYears(1);
        var deleted = store.DeleteRange(farFuture, farFuture.AddDays(1));

        Assert.Equal(0, deleted);
        Assert.Single(store.QuerySessions("steam:1091500", 10));
    }

    [Fact]
    public void DeleteRange_SessionsExactlyOnFromAndToBoundaries_AreBothIncluded()
    {
        var from = DateOnly.FromDateTime(DateTime.Now);
        var to = from.AddDays(2);
        var outside = from.AddDays(3);

        using var store = new BinaryFpsSessionStore(_dir);
        var onFrom = Session(startedUtcMs: UtcMsForLocalDate(from), endedUtcMs: UtcMsForLocalDate(from) + 600_000);
        var onTo = Session(startedUtcMs: UtcMsForLocalDate(to), endedUtcMs: UtcMsForLocalDate(to) + 600_000);
        var afterTo = Session(startedUtcMs: UtcMsForLocalDate(outside), endedUtcMs: UtcMsForLocalDate(outside) + 600_000);
        store.Append(onFrom);
        store.Append(onTo);
        store.Append(afterTo);

        var deleted = store.DeleteRange(from, to);

        Assert.Equal(2, deleted);
        var remaining = store.QuerySessions("steam:1091500", 10);
        Assert.Single(remaining);
        Assert.Equal(afterTo.Id, remaining[0].Id);
    }

    [Theory]
    [InlineData("0001-01-01", "0001-01-02")]
    [InlineData("9999-12-30", "9999-12-31")]
    public void DeleteRange_NearDateOnlyBounds_DoesNotThrow(string fromStr, string toStr)
    {
        using var store = new BinaryFpsSessionStore(_dir);
        store.Append(Session());

        var deleted = store.DeleteRange(DateOnly.Parse(fromStr), DateOnly.Parse(toStr));

        Assert.Equal(0, deleted);
        Assert.Single(store.QuerySessions("steam:1091500", 10));
    }

    [Fact]
    public void QueryPendingForUpload_ReturnsOnlyPendingSessions_OldestFirst()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        var older = Session(startedUtcMs: NowUtcMs, endedUtcMs: NowUtcMs + 600_000);
        var newer = Session(startedUtcMs: NowUtcMs + 1_000_000, endedUtcMs: NowUtcMs + 1_600_000);
        store.Append(older);
        store.Append(newer);
        store.MarkUploadState(new[] { newer.Id }, FpsUploadState.Sent);

        var pending = store.QueryPendingForUpload(10);

        var row = Assert.Single(pending);
        Assert.Equal(older.Id, row.Id);
    }

    [Fact]
    public void QueryPendingForUpload_RespectsMax()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        for (var i = 0; i < 5; i++)
        {
            store.Append(Session(startedUtcMs: NowUtcMs + i * 1000, endedUtcMs: NowUtcMs + i * 1000 + 600_000));
        }

        Assert.Equal(3, store.QueryPendingForUpload(3).Count);
    }

    [Fact]
    public void MarkUploadState_RoundTripsAcrossReopen()
    {
        FpsSessionRecord record;
        using (var store = new BinaryFpsSessionStore(_dir))
        {
            record = Session();
            store.Append(record);
            store.MarkUploadState(new[] { record.Id }, FpsUploadState.Sent);
        }

        using var reopened = new BinaryFpsSessionStore(_dir);
        var stored = Assert.Single(reopened.QuerySessions(record.GameKey, 10));
        Assert.Equal(FpsUploadState.Sent, stored.UploadState);
        Assert.Empty(reopened.QueryPendingForUpload(10));
    }

    [Fact]
    public void MarkUploadState_AcrossTwoMonths_UpdatesBoth()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        var thisMonth = Session(startedUtcMs: NowUtcMs, endedUtcMs: NowUtcMs + 600_000);
        var lastMonth = Session(startedUtcMs: PreviousMonthUtcMs, endedUtcMs: PreviousMonthUtcMs + 600_000);
        store.Append(thisMonth);
        store.Append(lastMonth);

        store.MarkUploadState(new[] { thisMonth.Id, lastMonth.Id }, FpsUploadState.Rejected);

        Assert.Empty(store.QueryPendingForUpload(10));
        Assert.All(store.QuerySessions(thisMonth.GameKey, 10), r => Assert.Equal(FpsUploadState.Rejected, r.UploadState));
    }

    [Fact]
    public void MarkUploadState_UnknownId_IsANoOp()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        var record = Session();
        store.Append(record);

        store.MarkUploadState(new[] { Guid.NewGuid() }, FpsUploadState.Sent);

        Assert.Single(store.QueryPendingForUpload(10));
    }

    [Fact]
    public void MarkUploadState_EmptyIdList_IsANoOp()
    {
        using var store = new BinaryFpsSessionStore(_dir);
        store.Append(Session());

        store.MarkUploadState(Array.Empty<Guid>(), FpsUploadState.Sent);

        Assert.Single(store.QueryPendingForUpload(10));
    }

    [Fact]
    public void Reopen_SeesSessionsPersistedByThePreviousInstance()
    {
        using (var store = new BinaryFpsSessionStore(_dir))
        {
            store.Append(Session());
        }

        using var reopened = new BinaryFpsSessionStore(_dir);
        Assert.Single(reopened.QuerySessions("steam:1091500", 10));
    }
}
