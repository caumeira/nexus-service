using Nexus.Service.Games;

namespace Nexus.Service.Tests.Games;

public class FpsHistogramTests
{
    [Fact]
    public void EdgeFps_FirstAndLastEdgesMatchTheDecidedRange()
    {
        Assert.Equal(1.0, FpsHistogram.EdgeFps(0), 6);
        Assert.Equal(1000.0, FpsHistogram.EdgeFps(FpsHistogram.BucketCount), 6);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(999, 63)]
    [InlineData(1000, 63)]
    public void BucketIndex_BoundaryValues(int fps, int expectedBucket)
    {
        Assert.Equal(expectedBucket, FpsHistogram.BucketIndex(fps));
    }

    [Fact]
    public void BucketIndex_IsMonotonicNonDecreasing()
    {
        var previous = -1;
        for (var fps = 1; fps <= 1000; fps++)
        {
            var bucket = FpsHistogram.BucketIndex(fps);
            Assert.InRange(bucket, previous, FpsHistogram.BucketCount - 1);
            previous = bucket;
        }
    }

    [Fact]
    public void BucketIndex_ClampsOutOfRangeInput()
    {
        Assert.Equal(0, FpsHistogram.BucketIndex(0));
        Assert.Equal(0, FpsHistogram.BucketIndex(-5));
        Assert.Equal(FpsHistogram.BucketCount - 1, FpsHistogram.BucketIndex(5000));
    }

    [Fact]
    public void AddSample_IncrementsTheOwningBucket()
    {
        var hist = new uint[FpsHistogram.BucketCount];
        FpsHistogram.AddSample(hist, 60);
        FpsHistogram.AddSample(hist, 60);

        Assert.Equal(2u, hist[FpsHistogram.BucketIndex(60)]);
        Assert.Equal(2, FpsHistogram.Total(hist));
    }

    [Fact]
    public void Merge_IsBucketWiseSum()
    {
        var a = new uint[FpsHistogram.BucketCount];
        var b = new uint[FpsHistogram.BucketCount];
        a[5] = 3;
        b[5] = 4;
        a[10] = 1;

        var merged = FpsHistogram.Merge(a, b);

        Assert.Equal(7u, merged[5]);
        Assert.Equal(1u, merged[10]);
        Assert.Equal(8, FpsHistogram.Total(merged));
    }

    [Fact]
    public void Percentile_EmptyHistogram_ReturnsZero()
    {
        Assert.Equal(0, FpsHistogram.Percentile(new uint[FpsHistogram.BucketCount], 50));
    }

    [Fact]
    public void Percentile_AllSamplesInOneBucket_ReturnsThatBucketsEdge()
    {
        var hist = new uint[FpsHistogram.BucketCount];
        var bucket = FpsHistogram.BucketIndex(60);
        hist[bucket] = 100;

        var p50 = FpsHistogram.Percentile(hist, 50);

        Assert.Equal((int)Math.Round(FpsHistogram.EdgeFps(bucket)), p50);
    }

    [Fact]
    public void Percentile_LowAndHighEndsPickDifferentBuckets()
    {
        var hist = new uint[FpsHistogram.BucketCount];
        FpsHistogram.AddSample(hist, 30);
        FpsHistogram.AddSample(hist, 144);

        var p1 = FpsHistogram.Percentile(hist, 1);
        var p99 = FpsHistogram.Percentile(hist, 99);

        Assert.True(p1 < p99);
    }
}
