using Nexus.Service.Panel.Streams;
using Xunit;

namespace Nexus.Service.Tests.Panel.Streams;

public class PacingPolicyTests
{
    [Fact]
    public void Waiting_for_idr_with_none_queued_holds()
    {
        var decision = PacingPolicy.Decide(queueDepth: 4, framesUntilIdr: -1, waitingForIdr: true);

        Assert.Equal(new PacingDecision(0, 0, false), decision);
    }

    [Fact]
    public void Waiting_for_idr_with_empty_queue_holds()
    {
        var decision = PacingPolicy.Decide(queueDepth: 0, framesUntilIdr: -1, waitingForIdr: true);

        Assert.Equal(new PacingDecision(0, 0, false), decision);
    }

    [Fact]
    public void Waiting_for_idr_drops_up_to_idr_and_sends_one_when_remaining_at_or_below_catch_up_depth()
    {
        var decision = PacingPolicy.Decide(queueDepth: 3, framesUntilIdr: 1, waitingForIdr: true);

        Assert.Equal(new PacingDecision(DropCount: 1, SendCount: 1, ClearWaitingForIdr: true), decision);
    }

    [Fact]
    public void Waiting_for_idr_at_head_drops_nothing_and_sends_from_idr()
    {
        var decision = PacingPolicy.Decide(queueDepth: PacingPolicy.CatchUpDepth, framesUntilIdr: 0, waitingForIdr: true);

        Assert.Equal(new PacingDecision(DropCount: 0, SendCount: 1, ClearWaitingForIdr: true), decision);
    }

    [Fact]
    public void Waiting_for_idr_with_remaining_exactly_catch_up_depth_sends_one()
    {
        var decision = PacingPolicy.Decide(queueDepth: PacingPolicy.CatchUpDepth + 2, framesUntilIdr: 2, waitingForIdr: true);

        Assert.Equal(new PacingDecision(DropCount: 2, SendCount: 1, ClearWaitingForIdr: true), decision);
    }

    [Fact]
    public void Waiting_for_idr_with_remaining_above_catch_up_depth_sends_two()
    {
        var decision = PacingPolicy.Decide(queueDepth: 9, framesUntilIdr: 2, waitingForIdr: true);

        Assert.Equal(new PacingDecision(DropCount: 2, SendCount: 2, ClearWaitingForIdr: true), decision);
    }

    [Fact]
    public void Waiting_for_idr_at_tail_drops_all_but_the_idr_and_sends_one()
    {
        var decision = PacingPolicy.Decide(queueDepth: 5, framesUntilIdr: 4, waitingForIdr: true);

        Assert.Equal(new PacingDecision(DropCount: 4, SendCount: 1, ClearWaitingForIdr: true), decision);
    }

    [Fact]
    public void Not_waiting_with_empty_queue_sends_nothing()
    {
        var decision = PacingPolicy.Decide(queueDepth: 0, framesUntilIdr: -1, waitingForIdr: false);

        Assert.Equal(new PacingDecision(0, 0, false), decision);
    }

    [Fact]
    public void Not_waiting_with_single_frame_sends_one()
    {
        var decision = PacingPolicy.Decide(queueDepth: 1, framesUntilIdr: 0, waitingForIdr: false);

        Assert.Equal(new PacingDecision(0, 1, false), decision);
    }

    [Fact]
    public void Not_waiting_at_catch_up_depth_sends_one()
    {
        var decision = PacingPolicy.Decide(queueDepth: PacingPolicy.CatchUpDepth, framesUntilIdr: -1, waitingForIdr: false);

        Assert.Equal(new PacingDecision(0, 1, false), decision);
    }

    [Fact]
    public void Not_waiting_above_catch_up_depth_sends_two()
    {
        var decision = PacingPolicy.Decide(queueDepth: PacingPolicy.CatchUpDepth + 1, framesUntilIdr: -1, waitingForIdr: false);

        Assert.Equal(new PacingDecision(0, 2, false), decision);
    }

    [Fact]
    public void Send_count_never_exceeds_queue_depth()
    {
        var decision = PacingPolicy.Decide(queueDepth: 1, framesUntilIdr: -1, waitingForIdr: false);

        Assert.True(decision.SendCount <= 1);
    }

    [Fact]
    public void Decisions_never_carry_negative_counts()
    {
        var holding = PacingPolicy.Decide(queueDepth: 0, framesUntilIdr: -1, waitingForIdr: true);
        Assert.True(holding.DropCount >= 0);
        Assert.True(holding.SendCount >= 0);

        var resyncing = PacingPolicy.Decide(queueDepth: 3, framesUntilIdr: 0, waitingForIdr: true);
        Assert.True(resyncing.DropCount >= 0);
        Assert.True(resyncing.SendCount >= 0);

        var steady = PacingPolicy.Decide(queueDepth: 10, framesUntilIdr: -1, waitingForIdr: false);
        Assert.True(steady.DropCount >= 0);
        Assert.True(steady.SendCount >= 0);
    }
}
