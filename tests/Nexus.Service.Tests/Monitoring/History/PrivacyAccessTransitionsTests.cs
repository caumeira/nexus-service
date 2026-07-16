using Nexus.Service.Monitoring.History;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

public class PrivacyAccessTransitionsTests
{
    // FILETIME values chosen only so ToUnixSeconds converts to round seconds;
    // FileTimeConversionTests covers the actual epoch math.
    private const long StartFileTime = 116_444_736_100_000_000; // 10s past the Unix epoch
    private const long StopFileTime = 116_444_736_800_000_000;  // 80s past the Unix epoch
    private const long LaterStartFileTime = 116_444_737_000_000_000; // 100s past the Unix epoch
    private const long LaterStopFileTime = 116_444_737_300_000_000;  // 130s past the Unix epoch
    private const long PollUtcSec = 1_000_000;

    private static PrivacyAccessRawEntry Entry(long start, long stop, string capability = "microphone", string appId = "app.exe") =>
        new(capability, appId, start, stop);

    [Fact]
    public void Advance_OpensASession_WhenStopIsZeroAndNothingIsTracked()
    {
        var transitions = new PrivacyAccessTransitions();

        var updates = transitions.Advance(new[] { Entry(StartFileTime, 0) }, PollUtcSec);

        var update = Assert.Single(updates);
        Assert.Equal("microphone", update.Capability);
        Assert.Equal("app.exe", update.AppId);
        Assert.Equal(10, update.StartUtcSec);
        Assert.Null(update.EndUtcSec);
    }

    [Fact]
    public void Advance_DoesNotReopen_WhenTheSameOpenSessionIsSeenAgain()
    {
        var transitions = new PrivacyAccessTransitions();
        transitions.Advance(new[] { Entry(StartFileTime, 0) }, PollUtcSec);

        var updates = transitions.Advance(new[] { Entry(StartFileTime, 0) }, PollUtcSec);

        Assert.Empty(updates);
    }

    [Fact]
    public void Advance_ClosesTheOpenSession_WhenStopBecomesNonZero()
    {
        var transitions = new PrivacyAccessTransitions();
        transitions.Advance(new[] { Entry(StartFileTime, 0) }, PollUtcSec);

        var updates = transitions.Advance(new[] { Entry(StartFileTime, StopFileTime) }, PollUtcSec);

        var update = Assert.Single(updates);
        Assert.Equal(10, update.StartUtcSec);
        Assert.Equal(80, update.EndUtcSec);
    }

    [Fact]
    public void Advance_DoesNotReclose_WhenTheSameClosedSessionIsSeenAgain()
    {
        var transitions = new PrivacyAccessTransitions();
        transitions.Advance(new[] { Entry(StartFileTime, 0) }, PollUtcSec);
        transitions.Advance(new[] { Entry(StartFileTime, StopFileTime) }, PollUtcSec);

        var updates = transitions.Advance(new[] { Entry(StartFileTime, StopFileTime) }, PollUtcSec);

        Assert.Empty(updates);
    }

    [Fact]
    public void Advance_RecordsACompleteMissedSession_WhenFirstSeenAlreadyClosed()
    {
        var transitions = new PrivacyAccessTransitions();

        var updates = transitions.Advance(new[] { Entry(StartFileTime, StopFileTime) }, PollUtcSec);

        var update = Assert.Single(updates);
        Assert.Equal(10, update.StartUtcSec);
        Assert.Equal(80, update.EndUtcSec);
    }

    [Fact]
    public void Advance_OpensANewSession_WhenStartAdvancesPastAClosedOne()
    {
        var transitions = new PrivacyAccessTransitions();
        transitions.Advance(new[] { Entry(StartFileTime, StopFileTime) }, PollUtcSec);

        var updates = transitions.Advance(new[] { Entry(LaterStartFileTime, 0) }, PollUtcSec);

        var update = Assert.Single(updates);
        Assert.Equal(100, update.StartUtcSec);
        Assert.Null(update.EndUtcSec);
    }

    // BUG regression: release+re-acquire within one poll window. Superseding
    // an OPEN session (not a closed one) must close the superseded row - the
    // registry only ever exposes the latest (start, stop) pair per key, so
    // this is the only chance to record that the old session ended.
    [Fact]
    public void Advance_ClosesTheSupersededSession_WhenANewSessionOpensWhileTheOldOneWasStillOpen()
    {
        var transitions = new PrivacyAccessTransitions();
        transitions.Advance(new[] { Entry(StartFileTime, 0) }, PollUtcSec); // opens at 10, never seen closing

        var updates = transitions.Advance(new[] { Entry(LaterStartFileTime, 0) }, PollUtcSec); // released, re-acquired

        Assert.Equal(2, updates.Count);
        var closedOld = Assert.Single(updates, u => u.StartUtcSec == 10);
        Assert.Equal(100, closedOld.EndUtcSec); // closed at the new session's start
        var openedNew = Assert.Single(updates, u => u.StartUtcSec == 100);
        Assert.Null(openedNew.EndUtcSec);
    }

    [Fact]
    public void Advance_ClosesTheSupersededOpenSession_WhenTheNewSessionIsAlreadyClosedToo()
    {
        var transitions = new PrivacyAccessTransitions();
        transitions.Advance(new[] { Entry(StartFileTime, 0) }, PollUtcSec); // open, never seen closing

        var updates = transitions.Advance(new[] { Entry(LaterStartFileTime, LaterStopFileTime) }, PollUtcSec);

        Assert.Equal(2, updates.Count);
        var closedOld = Assert.Single(updates, u => u.StartUtcSec == 10);
        Assert.Equal(100, closedOld.EndUtcSec);
        var missedNew = Assert.Single(updates, u => u.StartUtcSec == 100);
        Assert.Equal(130, missedNew.EndUtcSec);
    }

    // BUG regression: console user logoff/switch mid-session - the reader
    // reports nothing at all (empty snapshot) while a session was open.
    [Fact]
    public void Advance_ClosesAnOpenSession_WhenTheEntireSnapshotGoesEmpty()
    {
        var transitions = new PrivacyAccessTransitions();
        transitions.Advance(new[] { Entry(StartFileTime, 0) }, PollUtcSec);

        var updates = transitions.Advance(System.Array.Empty<PrivacyAccessRawEntry>(), PollUtcSec + 50);

        var update = Assert.Single(updates);
        Assert.Equal("microphone", update.Capability);
        Assert.Equal("app.exe", update.AppId);
        Assert.Equal(10, update.StartUtcSec);
        Assert.Equal(PollUtcSec + 50, update.EndUtcSec);
    }

    // BUG regression: ConsentStore entry removed (uninstall/privacy reset) -
    // one key vanishes while another, unrelated key keeps polling normally.
    [Fact]
    public void Advance_ClosesOnlyTheDisappearedKey_WhenAnotherKeyStillReportsOpen()
    {
        var transitions = new PrivacyAccessTransitions();
        transitions.Advance(new[]
        {
            Entry(StartFileTime, 0, "microphone", "removed.exe"),
            Entry(StartFileTime, 0, "microphone", "still-open.exe"),
        }, PollUtcSec);

        var updates = transitions.Advance(new[]
        {
            Entry(StartFileTime, 0, "microphone", "still-open.exe"),
        }, PollUtcSec + 50);

        var update = Assert.Single(updates);
        Assert.Equal("removed.exe", update.AppId);
        Assert.Equal(PollUtcSec + 50, update.EndUtcSec);
    }

    [Fact]
    public void Advance_DoesNotReclose_WhenADisappearedKeyStaysAbsent()
    {
        var transitions = new PrivacyAccessTransitions();
        transitions.Advance(new[] { Entry(StartFileTime, 0) }, PollUtcSec);
        transitions.Advance(System.Array.Empty<PrivacyAccessRawEntry>(), PollUtcSec + 50);

        var updates = transitions.Advance(System.Array.Empty<PrivacyAccessRawEntry>(), PollUtcSec + 100);

        Assert.Empty(updates);
    }

    [Fact]
    public void Advance_DoesNotCloseAnAlreadyClosedKey_WhenItDisappears()
    {
        var transitions = new PrivacyAccessTransitions();
        transitions.Advance(new[] { Entry(StartFileTime, StopFileTime) }, PollUtcSec); // closed on first sight

        var updates = transitions.Advance(System.Array.Empty<PrivacyAccessRawEntry>(), PollUtcSec + 50);

        Assert.Empty(updates);
    }

    // SMELL regression: a non-zero but unconvertible (negative/corrupt) stop
    // value must not wedge the session closed with no real end time.
    [Fact]
    public void Advance_LeavesTheSessionOpen_WhenTheStopValueIsCorruptOnAnAlreadyTrackedSession()
    {
        var transitions = new PrivacyAccessTransitions();
        transitions.Advance(new[] { Entry(StartFileTime, 0) }, PollUtcSec);

        var updates = transitions.Advance(new[] { Entry(StartFileTime, -1) }, PollUtcSec);

        Assert.Empty(updates);

        // A later valid stop still closes it normally.
        var closeUpdates = transitions.Advance(new[] { Entry(StartFileTime, StopFileTime) }, PollUtcSec);
        var update = Assert.Single(closeUpdates);
        Assert.Equal(80, update.EndUtcSec);
    }

    [Fact]
    public void Advance_TreatsACorruptStopOnANewSession_AsStillOpen()
    {
        var transitions = new PrivacyAccessTransitions();

        var updates = transitions.Advance(new[] { Entry(StartFileTime, -1) }, PollUtcSec);

        var update = Assert.Single(updates);
        Assert.Equal(10, update.StartUtcSec);
        Assert.Null(update.EndUtcSec);

        // The disappearance pass must not immediately re-close it either -
        // this same poll already recorded it as open, not absent.
        Assert.DoesNotContain(updates, u => u.EndUtcSec == PollUtcSec);
    }

    [Fact]
    public void Advance_RecordsTheMissedSession_WhenStartAdvancesPastAnOpenSessionAndIsAlreadyClosed()
    {
        var transitions = new PrivacyAccessTransitions();
        transitions.Advance(new[] { Entry(StartFileTime, 0) }, PollUtcSec); // open, never seen closing

        var updates = transitions.Advance(new[] { Entry(LaterStartFileTime, LaterStopFileTime) }, PollUtcSec);

        Assert.Equal(2, updates.Count); // closes the superseded open session too
        var missed = Assert.Single(updates, u => u.StartUtcSec == 100);
        Assert.Equal(130, missed.EndUtcSec);
    }

    [Fact]
    public void Advance_TracksMultipleCapabilitiesAndAppsIndependently()
    {
        var transitions = new PrivacyAccessTransitions();

        var updates = transitions.Advance(new[]
        {
            Entry(StartFileTime, 0, "microphone", "app1.exe"),
            Entry(StartFileTime, 0, "webcam", "app1.exe"),
            Entry(StartFileTime, 0, "microphone", "app2.exe"),
        }, PollUtcSec);

        Assert.Equal(3, updates.Count);
        Assert.Contains(updates, u => u.Capability == "microphone" && u.AppId == "app1.exe");
        Assert.Contains(updates, u => u.Capability == "webcam" && u.AppId == "app1.exe");
        Assert.Contains(updates, u => u.Capability == "microphone" && u.AppId == "app2.exe");
    }

    [Fact]
    public void Advance_ClosingOneAppDoesNotAffectAnotherAppsOpenSession()
    {
        var transitions = new PrivacyAccessTransitions();
        transitions.Advance(new[]
        {
            Entry(StartFileTime, 0, "microphone", "app1.exe"),
            Entry(StartFileTime, 0, "microphone", "app2.exe"),
        }, PollUtcSec);

        var updates = transitions.Advance(new[]
        {
            Entry(StartFileTime, StopFileTime, "microphone", "app1.exe"),
            Entry(StartFileTime, 0, "microphone", "app2.exe"),
        }, PollUtcSec);

        var update = Assert.Single(updates);
        Assert.Equal("app1.exe", update.AppId);
        Assert.Equal(80, update.EndUtcSec);
    }

    [Fact]
    public void Advance_IgnoresAnEntryWithNoValidStart()
    {
        var transitions = new PrivacyAccessTransitions();

        var updates = transitions.Advance(new[] { Entry(0, 0) }, PollUtcSec);

        Assert.Empty(updates);
    }

    [Fact]
    public void Advance_ReturnsEmpty_ForAnEmptySnapshot()
    {
        var transitions = new PrivacyAccessTransitions();

        Assert.Empty(transitions.Advance(System.Array.Empty<PrivacyAccessRawEntry>(), PollUtcSec));
    }
}
