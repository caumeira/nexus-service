using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Diagnostics.Temperature;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Temperature;

public class TemperatureInsightsTests
{
    private const long BucketMs = TemperatureSampler.BucketMinutes * 60_000L;
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly long T0Ms = new DateTimeOffset(T0).ToUnixTimeMilliseconds();

    private static TemperatureBucketRow Row(string id, string kind, int bucketIndex, double avg, double max = -1) =>
        new(id, kind, "GPU", T0Ms + bucketIndex * BucketMs, avg, max < 0 ? avg : max, 10);

    [Fact]
    public void SingleBucketAboveThreshold_ProducesNoEpisode()
    {
        var rows = new[] { Row("gpu:0", "gpu", 0, 90) };

        var episodes = TemperatureInsights.DetectEpisodes(rows);

        Assert.Empty(episodes);
    }

    [Fact]
    public void TwoConsecutiveContiguousBuckets_ProducesOneEpisode()
    {
        var rows = new[]
        {
            Row("gpu:0", "gpu", 0, 86, max: 90),
            Row("gpu:0", "gpu", 1, 88, max: 92),
        };

        var episodes = TemperatureInsights.DetectEpisodes(rows);

        var episode = Assert.Single(episodes);
        Assert.Equal("gpu:0", episode.ComponentId);
        Assert.Equal(85, episode.ThresholdC);
        Assert.Equal(92, episode.PeakC);
        Assert.Equal(T0, episode.StartUtc);
        Assert.Equal(T0.AddMilliseconds(2 * BucketMs), episode.EndUtc);
    }

    [Fact]
    public void GapBetweenAboveThresholdBuckets_ResetsTheStreak()
    {
        // Bucket 0 and bucket 2 are both above threshold but bucket 1 (the
        // adjacent slot) is missing entirely, so they never form one run.
        var rows = new[]
        {
            Row("gpu:0", "gpu", 0, 86),
            Row("gpu:0", "gpu", 2, 86),
        };

        var episodes = TemperatureInsights.DetectEpisodes(rows);

        Assert.Empty(episodes);
    }

    [Fact]
    public void ExactlyAtThreshold_CountsAsAboveThreshold()
    {
        var rows = new[]
        {
            Row("gpu:0", "gpu", 0, 85),
            Row("gpu:0", "gpu", 1, 85),
        };

        var episodes = TemperatureInsights.DetectEpisodes(rows);

        Assert.Single(episodes);
    }

    [Fact]
    public void BelowThreshold_NeverSustainsEvenIfConsecutive()
    {
        var rows = new[]
        {
            Row("gpu:0", "gpu", 0, 84.9),
            Row("gpu:0", "gpu", 1, 84.9),
            Row("gpu:0", "gpu", 2, 84.9),
        };

        Assert.Empty(TemperatureInsights.DetectEpisodes(rows));
    }

    [Fact]
    public void DifferentComponents_AreEvaluatedIndependently()
    {
        var rows = new[]
        {
            Row("cpu", "cpu", 0, 91),
            Row("cpu", "cpu", 1, 92),
            Row("storage:abc", "storage", 0, 40),
            Row("storage:abc", "storage", 1, 40),
        };

        var episodes = TemperatureInsights.DetectEpisodes(rows);

        var episode = Assert.Single(episodes);
        Assert.Equal("cpu", episode.ComponentId);
    }

    [Fact]
    public void UnknownKind_IsSkipped()
    {
        var rows = new[] { Row("x", "unknown", 0, 999), Row("x", "unknown", 1, 999) };

        Assert.Empty(TemperatureInsights.DetectEpisodes(rows));
    }

    private static List<TemperatureBucketRow> SeriesOf(int count, double avg = 50, double max = 55, int samples = 10) =>
        Enumerable.Range(0, count)
            .Select(i => new TemperatureBucketRow("cpu", "cpu", "CPU", T0Ms + i * BucketMs, avg, max, samples))
            .ToList();

    [Fact]
    public void Decimate_WhenUnderCap_ReturnsOnePointPerBucket()
    {
        var rows = SeriesOf(10);

        var points = TemperatureInsights.Decimate(rows, 600);

        Assert.Equal(10, points.Count);
    }

    [Fact]
    public void Decimate_WhenOverCap_RespectsMaxPoints()
    {
        var rows = SeriesOf(1000);

        var points = TemperatureInsights.Decimate(rows, 100);

        Assert.True(points.Count <= 100);
        Assert.True(points.Count > 0);
    }

    [Fact]
    public void Decimate_MergesWithSamplesWeightedAverage_AndMaxOfMaxes()
    {
        var rows = new List<TemperatureBucketRow>
        {
            new("cpu", "cpu", "CPU", T0Ms, AvgC: 10, MaxC: 20, Samples: 10),
            new("cpu", "cpu", "CPU", T0Ms + BucketMs, AvgC: 50, MaxC: 55, Samples: 30),
        };

        var points = TemperatureInsights.Decimate(rows, 1);

        var point = Assert.Single(points);
        // Weighted: (10*10 + 50*30) / 40 = 40.
        Assert.Equal(40, point.Avg, precision: 5);
        Assert.Equal(55, point.Max);
        Assert.Equal(T0Ms, point.T);
    }

    [Fact]
    public void Decimate_Empty_ReturnsEmpty()
    {
        Assert.Empty(TemperatureInsights.Decimate(Array.Empty<TemperatureBucketRow>(), 600));
    }
}
