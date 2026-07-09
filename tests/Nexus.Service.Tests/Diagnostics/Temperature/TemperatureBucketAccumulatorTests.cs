using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Diagnostics.Temperature;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Temperature;

public class TemperatureBucketAccumulatorTests
{
    private const long BucketMs = TemperatureSampler.BucketMinutes * 60_000L;

    private static (string, string, string, double) Reading(string id, double valueC, string kind = "cpu", string name = "CPU") =>
        (id, kind, name, valueC);

    [Fact]
    public void SameBucket_NeverFlushesUntilRollover()
    {
        var acc = new TemperatureBucketAccumulator();

        for (var i = 0; i < 9; i++)
        {
            var flushed = acc.Advance(0, new[] { Reading("cpu", 50 + i) });
            Assert.Empty(flushed);
        }
    }

    [Fact]
    public void Rollover_FlushesAverageMaxAndSampleCount()
    {
        var acc = new TemperatureBucketAccumulator();
        var readings = new double[] { 40, 42, 44, 46, 48, 50, 52, 54, 56, 58 };

        foreach (var value in readings)
        {
            acc.Advance(0, new[] { Reading("cpu", value) });
        }

        var flushed = acc.Advance(BucketMs, new[] { Reading("cpu", 60) });

        var row = Assert.Single(flushed);
        Assert.Equal("cpu", row.ComponentId);
        Assert.Equal("cpu", row.Kind);
        Assert.Equal("CPU", row.Name);
        Assert.Equal(0, row.BucketUtcMs);
        Assert.Equal(readings.Average(), row.AvgC, precision: 5);
        Assert.Equal(58, row.MaxC);
        Assert.Equal(10, row.Samples);
    }

    [Fact]
    public void ComponentThatStopsReporting_StillFlushesOnNextRollover()
    {
        var acc = new TemperatureBucketAccumulator();
        acc.Advance(0, new[] { Reading("storage:abc", 30), Reading("storage:abc", 34) });

        // storage:abc reports nothing this tick, but its bucket has passed.
        var flushed = acc.Advance(BucketMs, Enumerable.Empty<(string, string, string, double)>());

        var row = Assert.Single(flushed);
        Assert.Equal("storage:abc", row.ComponentId);
        Assert.Equal(32, row.AvgC);
        Assert.Equal(2, row.Samples);
    }

    [Fact]
    public void MultipleComponents_AccumulateIndependently()
    {
        var acc = new TemperatureBucketAccumulator();
        acc.Advance(0, new[] { Reading("cpu", 50), Reading("gpu:0", 70, "gpu", "GPU") });
        acc.Advance(0, new[] { Reading("cpu", 60), Reading("gpu:0", 80, "gpu", "GPU") });

        var flushed = acc.Advance(BucketMs, Enumerable.Empty<(string, string, string, double)>());

        Assert.Equal(2, flushed.Count);
        var cpu = flushed.Single(r => r.ComponentId == "cpu");
        Assert.Equal(55, cpu.AvgC);
        var gpu = flushed.Single(r => r.ComponentId == "gpu:0");
        Assert.Equal(75, gpu.AvgC);
        Assert.Equal(80, gpu.MaxC);
    }

    [Fact]
    public void AllReadingsBelowZero_MaxReflectsTheHighestReading_NotZero()
    {
        var acc = new TemperatureBucketAccumulator();
        acc.Advance(0, new[] { Reading("cpu", -20), Reading("cpu", -5), Reading("cpu", -10) });

        var flushed = acc.Advance(BucketMs, Enumerable.Empty<(string, string, string, double)>());

        var row = Assert.Single(flushed);
        Assert.Equal(-5, row.MaxC);
        Assert.Equal(-35.0 / 3, row.AvgC, precision: 5);
    }

    [Fact]
    public void NewBucketAfterRollover_StartsFreshAccumulation()
    {
        var acc = new TemperatureBucketAccumulator();
        acc.Advance(0, new[] { Reading("cpu", 100) });
        acc.Advance(BucketMs, new[] { Reading("cpu", 10) });

        var flushed = acc.Advance(2 * BucketMs, Enumerable.Empty<(string, string, string, double)>());

        var row = Assert.Single(flushed);
        Assert.Equal(BucketMs, row.BucketUtcMs);
        Assert.Equal(10, row.AvgC);
        Assert.Equal(1, row.Samples);
    }
}
