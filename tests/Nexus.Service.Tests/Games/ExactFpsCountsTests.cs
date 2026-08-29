using Nexus.Service.Games;

namespace Nexus.Service.Tests.Games;

public class ExactFpsCountsTests
{
    private static uint[] NewCounts() => new uint[ExactFpsCounts.MaxFps + 1];

    [Fact]
    public void Percentile_EmptyCounts_ReturnsZero()
    {
        Assert.Equal(0, ExactFpsCounts.Percentile(NewCounts(), 50));
    }

    [Fact]
    public void Percentile_SingleValue_ReturnsThatExactValue()
    {
        var counts = NewCounts();
        ExactFpsCounts.Add(counts, 60);

        Assert.Equal(60, ExactFpsCounts.Percentile(counts, 10));
        Assert.Equal(60, ExactFpsCounts.Percentile(counts, 50));
        Assert.Equal(60, ExactFpsCounts.Percentile(counts, 90));
    }

    [Fact]
    public void Percentile_A60HzVsyncSession_DoesNotFalselyReadAsUncapped()
    {
        // The bug this class fixes: a 60 Hz v-sync session straddling
        // 54/60 fps spans two adjacent 64-bucket log buckets and would
        // otherwise read p90-p10 wider than the capped threshold.
        var counts = NewCounts();
        for (var i = 0; i < 100; i++)
        {
            ExactFpsCounts.Add(counts, i % 3 == 0 ? 59 : i % 3 == 1 ? 60 : 61);
        }

        var p10 = ExactFpsCounts.Percentile(counts, 10);
        var p90 = ExactFpsCounts.Percentile(counts, 90);

        Assert.True(p90 - p10 <= 2);
    }

    [Fact]
    public void Add_OutOfRangeFrames_IsIgnored()
    {
        var counts = NewCounts();
        ExactFpsCounts.Add(counts, -1);
        ExactFpsCounts.Add(counts, ExactFpsCounts.MaxFps + 1);

        Assert.Equal(0, ExactFpsCounts.Percentile(counts, 50));
    }

    [Fact]
    public void Percentile_WideSpread_ReflectsTheActualRange()
    {
        var counts = NewCounts();
        for (var fps = 30; fps <= 144; fps++)
        {
            ExactFpsCounts.Add(counts, fps);
        }

        var p10 = ExactFpsCounts.Percentile(counts, 10);
        var p90 = ExactFpsCounts.Percentile(counts, 90);

        Assert.True(p90 - p10 > 2);
    }
}
