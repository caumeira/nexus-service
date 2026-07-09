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
}
