using System.Linq;
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

    private static PrivacyAccessRawEntry Entry(long start, long stop, string capability = "microphone", string appId = "app.exe") =>
        new(capability, appId, start, stop);

    [Fact]
    public void Advance_OpensASession_WhenStopIsZeroAndNothingIsTracked()
    {
        var transitions = new PrivacyAccessTransitions();

        var updates = transitions.Advance(new[] { Entry(StartFileTime, 0) });

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
        transitions.Advance(new[] { Entry(StartFileTime, 0) });

        var updates = transitions.Advance(new[] { Entry(StartFileTime, 0) });

        Assert.Empty(updates);
    }

    [Fact]
    public void Advance_ClosesTheOpenSession_WhenStopBecomesNonZero()
    {
        var transitions = new PrivacyAccessTransitions();
        transitions.Advance(new[] { Entry(StartFileTime, 0) });

        var updates = transitions.Advance(new[] { Entry(StartFileTime, StopFileTime) });

        var update = Assert.Single(updates);
        Assert.Equal(10, update.StartUtcSec);
        Assert.Equal(80, update.EndUtcSec);
    }

    [Fact]
    public void Advance_DoesNotReclose_WhenTheSameClosedSessionIsSeenAgain()
    {
        var transitions = new PrivacyAccessTransitions();
        transitions.Advance(new[] { Entry(StartFileTime, 0) });
        transitions.Advance(new[] { Entry(StartFileTime, StopFileTime) });

        var updates = transitions.Advance(new[] { Entry(StartFileTime, StopFileTime) });

        Assert.Empty(updates);
    }

    [Fact]
    public void Advance_RecordsACompleteMissedSession_WhenFirstSeenAlreadyClosed()
    {
        var transitions = new PrivacyAccessTransitions();

        var updates = transitions.Advance(new[] { Entry(StartFileTime, StopFileTime) });

        var update = Assert.Single(updates);
        Assert.Equal(10, update.StartUtcSec);
        Assert.Equal(80, update.EndUtcSec);
    }

    [Fact]
    public void Advance_OpensANewSession_WhenStartAdvancesPastAClosedOne()
    {
        var transitions = new PrivacyAccessTransitions();
        transitions.Advance(new[] { Entry(StartFileTime, StopFileTime) });

        var updates = transitions.Advance(new[] { Entry(LaterStartFileTime, 0) });

        var update = Assert.Single(updates);
        Assert.Equal(100, update.StartUtcSec);
        Assert.Null(update.EndUtcSec);
    }

    [Fact]
    public void Advance_RecordsTheMissedSession_WhenStartAdvancesPastAnOpenSessionAndIsAlreadyClosed()
    {
        var transitions = new PrivacyAccessTransitions();
        transitions.Advance(new[] { Entry(StartFileTime, 0) }); // open, never seen closing

        var updates = transitions.Advance(new[] { Entry(LaterStartFileTime, LaterStopFileTime) });

        var update = Assert.Single(updates);
        Assert.Equal(100, update.StartUtcSec);
        Assert.Equal(130, update.EndUtcSec);
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
        });

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
        });

        var updates = transitions.Advance(new[]
        {
            Entry(StartFileTime, StopFileTime, "microphone", "app1.exe"),
            Entry(StartFileTime, 0, "microphone", "app2.exe"),
        });

        var update = Assert.Single(updates);
        Assert.Equal("app1.exe", update.AppId);
        Assert.Equal(80, update.EndUtcSec);
    }

    [Fact]
    public void Advance_IgnoresAnEntryWithNoValidStart()
    {
        var transitions = new PrivacyAccessTransitions();

        var updates = transitions.Advance(new[] { Entry(0, 0) });

        Assert.Empty(updates);
    }

    [Fact]
    public void Advance_ReturnsEmpty_ForAnEmptySnapshot()
    {
        var transitions = new PrivacyAccessTransitions();

        Assert.Empty(transitions.Advance(System.Array.Empty<PrivacyAccessRawEntry>()));
    }
}
