using Nexus.Service.Fps;

namespace Nexus.Service.Tests.Fps;

public class CompletedSecondsRingTests
{
    private static CompletedSecondsRing CreateRing(int size = 8, int completionLagSec = 2) => new(size, completionLagSec);

    [Fact]
    public void ReadCompleted_BeforeStartTracking_ReturnsNothing()
    {
        var ring = CreateRing();

        Assert.Empty(ring.ReadCompleted(long.MinValue, nowSec: 1000));
    }

    [Fact]
    public void ReadCompleted_NeverReportsASecondBeforeTrackingStarted()
    {
        var ring = CreateRing();
        ring.StartTracking(nowSec: 1000);

        var completed = ring.ReadCompleted(afterTsSec: 500, nowSec: 1003);

        Assert.All(completed, c => Assert.True(c.TsSec >= 1000));
    }

    [Fact]
    public void RecordFrame_ThenReadOnceItsCompletionLagHasPassed_ReturnsTheCount()
    {
        var ring = CreateRing();
        ring.StartTracking(nowSec: 1000);
        ring.RecordFrame(1000);
        ring.RecordFrame(1000);
        ring.RecordFrame(1000);

        // Not yet completed: only 1 second has passed, lag is 2.
        Assert.Empty(ring.ReadCompleted(afterTsSec: 999, nowSec: 1001));

        var completed = ring.ReadCompleted(afterTsSec: 999, nowSec: 1002);

        var entry = Assert.Single(completed);
        Assert.Equal(1000, entry.TsSec);
        Assert.Equal(3, entry.Frames);
    }

    [Fact]
    public void ReadCompleted_ASecondWithNoRecordedFrames_ReportsZero_NotMissing()
    {
        // Reproduces the "no present arrived at tick time" / paused-target
        // case: completion is decided by wall clock, not by a later frame
        // closing the bucket.
        var ring = CreateRing();
        ring.StartTracking(nowSec: 1000);
        ring.RecordFrame(1000);
        // Nothing recorded for 1001 - the target paused.

        var completed = ring.ReadCompleted(afterTsSec: 999, nowSec: 1004);

        Assert.Equal(new long[] { 1000, 1001, 1002 }, completed.Select(c => c.TsSec).ToArray());
        Assert.Equal(new[] { 1, 0, 0 }, completed.Select(c => c.Frames).ToArray());
    }

    [Fact]
    public void ReadCompleted_CalledTwiceWithTheCallersOwnCursor_NeverReturnsTheSameSecondTwice()
    {
        // Reproduces the "two ticks computing the same now-1" double-count:
        // each caller advances its own afterTsSec past what it already
        // consumed.
        var ring = CreateRing();
        ring.StartTracking(nowSec: 1000);
        ring.RecordFrame(1000);
        ring.RecordFrame(1001);

        var first = ring.ReadCompleted(afterTsSec: 999, nowSec: 1002);
        var lastConsumed = first.Max(c => c.TsSec);
        var second = ring.ReadCompleted(afterTsSec: lastConsumed, nowSec: 1002);

        Assert.Empty(second);
    }

    [Fact]
    public void ReadCompleted_ALateArrivingFrame_StillCountsOnceItsSecondCompletes()
    {
        // A frame for ts=1000 that arrives just before the completion
        // threshold still lands in the right bucket - completion never
        // depended on it arriving promptly.
        var ring = CreateRing();
        ring.StartTracking(nowSec: 1000);
        ring.RecordFrame(1000);
        ring.RecordFrame(1000); // arrives "late" relative to a slow poller, still same second

        var completed = ring.ReadCompleted(afterTsSec: 999, nowSec: 1002);

        Assert.Equal(2, Assert.Single(completed).Frames);
    }

    [Fact]
    public void ReadCompleted_CapsToOneRingWidthPerCall()
    {
        var ring = CreateRing(size: 8);
        ring.StartTracking(nowSec: 1000);

        var completed = ring.ReadCompleted(afterTsSec: 999, nowSec: 1000 + 100);

        Assert.Equal(8, completed.Count);
    }

    [Fact]
    public void Reset_StopsTrackingAndClearsRecordedFrames()
    {
        var ring = CreateRing();
        ring.StartTracking(nowSec: 1000);
        ring.RecordFrame(1000);

        ring.Reset();

        Assert.False(ring.IsTracking);
        Assert.Empty(ring.ReadCompleted(afterTsSec: long.MinValue, nowSec: 1002));
    }

    [Fact]
    public void StartTracking_AfterReset_DoesNotResurfaceStaleFramesFromTheOldTarget()
    {
        var ring = CreateRing();
        ring.StartTracking(nowSec: 1000);
        ring.RecordFrame(1000); // old target's frame

        ring.Reset();
        ring.StartTracking(nowSec: 1000); // new target happens to reuse the same second

        var completed = ring.ReadCompleted(afterTsSec: 999, nowSec: 1002);

        Assert.Equal(0, Assert.Single(completed).Frames);
    }
}
