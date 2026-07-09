using System.Linq;
using Nexus.Service.Diagnostics.Temperature;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Temperature;

public class InMemoryTemperatureHistoryStoreTests
{
    private static TemperatureBucketRow Row(string id, long bucketUtcMs, double avg = 50) =>
        new(id, "cpu", "CPU", bucketUtcMs, avg, avg + 5, 10);

    [Fact]
    public void UpsertThenQuery_RoundTrips()
    {
        var store = new InMemoryTemperatureHistoryStore();
        store.UpsertBuckets(new[] { Row("cpu", 1000, avg: 55) });

        var row = Assert.Single(store.Query(0, 10_000));
        Assert.Equal("cpu", row.ComponentId);
        Assert.Equal(55, row.AvgC);
    }

    [Fact]
    public void Upsert_SameKey_Replaces()
    {
        var store = new InMemoryTemperatureHistoryStore();
        store.UpsertBuckets(new[] { Row("cpu", 1000, avg: 50) });
        store.UpsertBuckets(new[] { Row("cpu", 1000, avg: 90) });

        Assert.Equal(90, Assert.Single(store.Query(0, 10_000)).AvgC);
    }

    [Fact]
    public void Query_ReturnsAscendingByBucketTime()
    {
        var store = new InMemoryTemperatureHistoryStore();
        store.UpsertBuckets(new[] { Row("cpu", 3000), Row("cpu", 1000), Row("cpu", 2000) });

        var ordered = store.Query(0, 10_000).Select(r => r.BucketUtcMs).ToList();
        Assert.Equal(new long[] { 1000, 2000, 3000 }, ordered);
    }

    [Fact]
    public void PruneOlderThan_RemovesOnlyOlderRows()
    {
        var store = new InMemoryTemperatureHistoryStore();
        store.UpsertBuckets(new[] { Row("cpu", 1000), Row("cpu", 3000) });

        var deleted = store.PruneOlderThan(2000);

        Assert.Equal(1, deleted);
        Assert.Equal(3000, Assert.Single(store.Query(0, 10_000)).BucketUtcMs);
    }

    private static TemperatureBucketRow GpuRow(string id, string name, long bucketUtcMs, double avg = 50, int samples = 10) =>
        new(id, "gpu", name, bucketUtcMs, avg, avg + 5, samples);

    [Fact]
    public void FindLegacyGpuComponentIds_ReturnsOnlyDigitSuffixIdsForThatName()
    {
        var store = new InMemoryTemperatureHistoryStore();
        store.UpsertBuckets(new[]
        {
            GpuRow("gpu:0", "NVIDIA GeForce RTX 5080", 1000),
            GpuRow("gpu:GPU-abc123", "NVIDIA GeForce RTX 5080", 1000),
            GpuRow("gpu:0", "AMD Radeon RX 7900", 2000),
        });

        var ids = store.FindLegacyGpuComponentIds("NVIDIA GeForce RTX 5080");

        Assert.Equal(new[] { "gpu:0" }, ids);
    }

    [Fact]
    public void FindLegacyGpuComponentIds_ReturnsMultiple_WhenAmbiguous()
    {
        var store = new InMemoryTemperatureHistoryStore();
        store.UpsertBuckets(new[]
        {
            GpuRow("gpu:0", "NVIDIA GeForce RTX 5080", 1000),
            GpuRow("gpu:1", "NVIDIA GeForce RTX 5080", 2000),
        });

        Assert.Equal(2, store.FindLegacyGpuComponentIds("NVIDIA GeForce RTX 5080").Count);
    }

    [Fact]
    public void RekeyComponent_MovesNonCollidingRows_AndRemovesOldId()
    {
        var store = new InMemoryTemperatureHistoryStore();
        store.UpsertBuckets(new[]
        {
            GpuRow("gpu:0", "NVIDIA GeForce RTX 5080", 1000, avg: 55),
            GpuRow("gpu:0", "NVIDIA GeForce RTX 5080", 2000, avg: 65),
        });

        var moved = store.RekeyComponent("gpu:0", "gpu:GPU-abc123");

        Assert.Equal(2, moved);
        var rows = store.Query(0, 10_000);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("gpu:GPU-abc123", r.ComponentId));
    }

    [Fact]
    public void RekeyComponent_Collision_KeepsRowWithMoreSamples()
    {
        var store = new InMemoryTemperatureHistoryStore();
        store.UpsertBuckets(new[] { GpuRow("gpu:0", "NVIDIA GeForce RTX 5080", 1000, avg: 55, samples: 20) });
        store.UpsertBuckets(new[] { GpuRow("gpu:GPU-abc123", "NVIDIA GeForce RTX 5080", 1000, avg: 70, samples: 5) });

        store.RekeyComponent("gpu:0", "gpu:GPU-abc123");

        var row = Assert.Single(store.Query(0, 10_000));
        Assert.Equal(55, row.AvgC);
        Assert.Equal(20, row.Samples);
    }

    [Fact]
    public void RekeyComponent_Collision_KeepsNewRow_WhenNewHasMoreOrEqualSamples()
    {
        var store = new InMemoryTemperatureHistoryStore();
        store.UpsertBuckets(new[] { GpuRow("gpu:0", "NVIDIA GeForce RTX 5080", 1000, avg: 55, samples: 5) });
        store.UpsertBuckets(new[] { GpuRow("gpu:GPU-abc123", "NVIDIA GeForce RTX 5080", 1000, avg: 70, samples: 20) });

        store.RekeyComponent("gpu:0", "gpu:GPU-abc123");

        var row = Assert.Single(store.Query(0, 10_000));
        Assert.Equal(70, row.AvgC);
        Assert.Equal(20, row.Samples);
    }

    [Fact]
    public void RekeyComponent_NoRows_ReturnsZero()
    {
        var store = new InMemoryTemperatureHistoryStore();
        Assert.Equal(0, store.RekeyComponent("gpu:0", "gpu:GPU-abc123"));
    }
}
