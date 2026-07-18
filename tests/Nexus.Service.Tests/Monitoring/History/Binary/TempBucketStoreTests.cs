using System;
using System.IO;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

/// <summary>
/// TempBucketStore-level coverage for behavior TemperatureBucketSpec (which
/// exercises it only indirectly, through BinaryMetricsHistoryStore at the
/// real 90-day capacity) cannot easily reach: a small, test-chosen bucket
/// capacity so wraparound is cheap to construct, RebuildBucket's
/// overwrite-not-accumulate contract and its skip-on-zero-readings guard in
/// isolation, and TempBucketTier.ToPruneFloorIndex's ceiling conversion for a
/// non-bucket-aligned cutoff. The source-retention guard that stops a
/// backward-dated replay from reaching RebuildBucket at all lives one level
/// up, in BinaryMetricsHistoryStore.RebuildTempBuckets - see
/// TemperatureBucketSpec's Append_BackwardDatedReplayIntoAnAgedOutBucket
/// test for that.
/// </summary>
public class TempBucketStoreTests : IDisposable
{
    private readonly string _dir;

    public TempBucketStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-tempbucketstore-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private TempBucketStore CreateStore(long bucketCapacity = 100, int entityCapacity = 10, long initialPruneFloorSec = long.MinValue) =>
        new(_dir, bucketCapacity, entityCapacity, initialPruneFloorSec);

    private static FieldAgg Agg(double sum, int cnt, double max) => new(sum, cnt, max);

    [Fact]
    public void RebuildBucket_ThenQuery_RoundTrips()
    {
        using var store = CreateStore();
        store.RebuildBucket("cpu", "cpu", "Test CPU", 0, Agg(sum: 100, cnt: 2, max: 60));

        var row = Assert.Single(store.Query(0, 0));

        Assert.Equal("cpu", row.ComponentId);
        Assert.Equal("cpu", row.Kind);
        Assert.Equal("Test CPU", row.Name);
        Assert.Equal(50, row.AvgC);
        Assert.Equal(60, row.MaxC);
        Assert.Equal(2, row.Samples);
    }

    [Fact]
    public void RebuildBucket_RewritingTheSameBucket_OverwritesRatherThanAccumulating()
    {
        using var store = CreateStore();
        store.RebuildBucket("cpu", "cpu", "Test CPU", 0, Agg(sum: 40, cnt: 1, max: 40));
        store.RebuildBucket("cpu", "cpu", "Test CPU", 0, Agg(sum: 180, cnt: 2, max: 99));

        var row = Assert.Single(store.Query(0, 0));

        Assert.Equal(90, row.AvgC); // 180/2, not folded with the earlier write
        Assert.Equal(99, row.MaxC);
        Assert.Equal(2, row.Samples);
    }

    [Fact]
    public void RebuildBucket_WithZeroReadings_LeavesAnExistingBucketUntouched()
    {
        using var store = CreateStore();
        store.RebuildBucket("cpu", "cpu", "Test CPU", 0, Agg(sum: 40, cnt: 1, max: 40));
        store.RebuildBucket("cpu", "cpu", "Test CPU", 0, FieldAgg.Empty);

        var row = Assert.Single(store.Query(0, 0));
        Assert.Equal(40, row.AvgC);
        Assert.Equal(1, row.Samples);
    }

    [Fact]
    public void RebuildBucket_WithZeroReadings_NeverCreatesANewBucket()
    {
        using var store = CreateStore();
        store.RebuildBucket("cpu", "cpu", "Test CPU", 0, FieldAgg.Empty);

        Assert.Empty(store.Query(0, 0));
    }

    [Fact]
    public void Query_ExcludesABucketWhoseStartIsOutsideTheWindow()
    {
        using var store = CreateStore();
        var width = TempBucketTier.SecondsPerBucket;
        store.RebuildBucket("cpu", "cpu", "Test CPU", 0, Agg(sum: 40, cnt: 1, max: 40));
        store.RebuildBucket("cpu", "cpu", "Test CPU", width, Agg(sum: 60, cnt: 1, max: 60));

        var rows = store.Query(0, width - 1);

        var row = Assert.Single(rows);
        Assert.Equal(0L, row.BucketUtcMs);
    }

    [Fact]
    public void Ring_WrapsPastBucketCapacity_OldBucketReadsAsAbsent()
    {
        using var store = CreateStore(bucketCapacity: 5);
        var width = TempBucketTier.SecondsPerBucket;
        store.RebuildBucket("cpu", "cpu", "Test CPU", 0, Agg(sum: 10, cnt: 1, max: 10));
        store.RebuildBucket("cpu", "cpu", "Test CPU", width * 5, Agg(sum: 20, cnt: 1, max: 20)); // bucket index 5 == index 0, mod 5

        var rows = store.Query(0, width * 5);

        Assert.DoesNotContain(rows, r => r.BucketUtcMs == 0);
        var survivor = Assert.Single(rows);
        Assert.Equal(width * 5 * 1000L, survivor.BucketUtcMs);
        Assert.Equal(20, survivor.AvgC);
    }

    [Fact]
    public void RaisePruneFloor_DropsABucketWhoseStartPrecedesANonBucketAlignedCutoff()
    {
        var width = TempBucketTier.SecondsPerBucket;
        using var store = CreateStore();
        store.RebuildBucket("cpu", "cpu", "Test CPU", 0, Agg(sum: 10, cnt: 1, max: 10));
        store.RebuildBucket("cpu", "cpu", "Test CPU", width, Agg(sum: 20, cnt: 1, max: 20));

        // cutoff=width-1 is not bucket-aligned; bucket 0's start (0) is
        // before it, bucket width's start is not - only the former drops.
        store.RaisePruneFloor(width - 1);

        var rows = store.Query(0, width * 2);
        Assert.DoesNotContain(rows, r => r.BucketUtcMs == 0);
        Assert.Contains(rows, r => r.BucketUtcMs == width * 1000L);
    }

    [Fact]
    public void RaisePruneFloor_NeverLowersAnAlreadyHigherFloor()
    {
        using var store = CreateStore();
        var width = TempBucketTier.SecondsPerBucket;
        store.RebuildBucket("cpu", "cpu", "Test CPU", 0, Agg(sum: 10, cnt: 1, max: 10));

        Assert.True(store.RaisePruneFloor(width * 2));
        Assert.False(store.RaisePruneFloor(width)); // lower: rejected
    }

    [Fact]
    public void Reopen_RecoversStableRingIndicesAndData()
    {
        using (var store = CreateStore())
        {
            store.RebuildBucket("cpu", "cpu", "Test CPU", 0, Agg(sum: 40, cnt: 1, max: 40));
        }

        using var reopened = CreateStore();
        var row = Assert.Single(reopened.Query(0, 0));
        Assert.Equal(40, row.AvgC);

        // A newly-seen key after reopen must land at the NEXT ring index,
        // not collide with cpu's already-reopened index 0.
        reopened.RebuildBucket("gpu:gpu-0", "gpu", "RTX 5080", 0, Agg(sum: 60, cnt: 1, max: 60));
        var rows = reopened.Query(0, 0);
        Assert.Contains(rows, r => r.ComponentId == "cpu" && r.AvgC == 40);
        Assert.Contains(rows, r => r.ComponentId == "gpu:gpu-0" && r.AvgC == 60);
    }
}
