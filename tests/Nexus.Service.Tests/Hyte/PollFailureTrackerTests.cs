using Nexus.Service.Peripherals.Hyte;

namespace Nexus.Service.Tests.Hyte;

public class PollFailureTrackerTests
{
    [Fact]
    public void Failures_below_the_threshold_do_not_ask_for_a_disconnect()
    {
        var tracker = new PollFailureTracker("test");

        for (var i = 1; i < PollFailureTracker.Threshold; i++)
        {
            Assert.False(tracker.ShouldDisconnect("poll", "desync"));
        }

        Assert.Equal(PollFailureTracker.Threshold - 1, tracker.Consecutive);
    }

    [Fact]
    public void The_threshold_failure_asks_for_a_disconnect_and_rearms()
    {
        var tracker = new PollFailureTracker("test");

        for (var i = 1; i < PollFailureTracker.Threshold; i++)
        {
            tracker.ShouldDisconnect("poll", "desync");
        }

        Assert.True(tracker.ShouldDisconnect("poll", "desync"));
        Assert.Equal(0, tracker.Consecutive);
    }

    [Fact]
    public void A_success_clears_the_run_so_isolated_failures_never_accumulate()
    {
        var tracker = new PollFailureTracker("test");

        for (var round = 0; round < 5; round++)
        {
            for (var i = 1; i < PollFailureTracker.Threshold; i++)
            {
                Assert.False(tracker.ShouldDisconnect("poll", "desync"));
            }
            tracker.Reset();
        }

        Assert.Equal(0, tracker.Consecutive);
    }
}
