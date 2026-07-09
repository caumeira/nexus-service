using System;
using System.IO;
using System.Linq;
using Nexus.Service.Diagnostics.Temperature;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Temperature;

public class SqliteTemperatureHistoryStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteTemperatureHistoryStore _store;

    public SqliteTemperatureHistoryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-temphistory-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _store = new SqliteTemperatureHistoryStore(Path.Combine(_dir, "temperature.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static TemperatureBucketRow Row(string id, long bucketUtcMs, double avg = 50, double max = 60, int samples = 10) =>
        new(id, "cpu", "CPU", bucketUtcMs, avg, max, samples);

    [Fact]
    public void UpsertThenQuery_RoundTripsAllFields()
    {
        _store.UpsertBuckets(new[] { new TemperatureBucketRow("cpu", "cpu", "AMD Ryzen 7", 1_000_000, 62.5, 71.0, 10) });

        var rows = _store.Query(0, 2_000_000);

        var row = Assert.Single(rows);
        Assert.Equal("cpu", row.ComponentId);
        Assert.Equal("cpu", row.Kind);
        Assert.Equal("AMD Ryzen 7", row.Name);
        Assert.Equal(1_000_000, row.BucketUtcMs);
        Assert.Equal(62.5, row.AvgC);
        Assert.Equal(71.0, row.MaxC);
        Assert.Equal(10, row.Samples);
    }

    [Fact]
    public void Upsert_SameComponentAndBucket_ReplacesRatherThanDuplicates()
    {
        _store.UpsertBuckets(new[] { Row("cpu", 1000, avg: 50) });
        _store.UpsertBuckets(new[] { Row("cpu", 1000, avg: 80) });

        var rows = _store.Query(0, 10_000);

        var row = Assert.Single(rows);
        Assert.Equal(80, row.AvgC);
    }

    [Fact]
    public void Query_ReturnsRowsAscendingByBucketTime()
    {
        _store.UpsertBuckets(new[]
        {
            Row("cpu", 3000),
            Row("cpu", 1000),
            Row("cpu", 2000),
        });

        var rows = _store.Query(0, 10_000);

        Assert.Equal(new long[] { 1000, 2000, 3000 }, rows.Select(r => r.BucketUtcMs).ToArray());
    }

    [Fact]
    public void Query_ExcludesRowsOutsideRange()
    {
        _store.UpsertBuckets(new[] { Row("cpu", 1000), Row("cpu", 5000), Row("cpu", 9000) });

        var rows = _store.Query(2000, 6000);

        Assert.Equal(5000, Assert.Single(rows).BucketUtcMs);
    }

    [Fact]
    public void PruneOlderThan_DeletesOnlyOlderRows_AndReturnsCount()
    {
        _store.UpsertBuckets(new[] { Row("cpu", 1000), Row("cpu", 2000), Row("cpu", 3000) });

        var deleted = _store.PruneOlderThan(2500);

        Assert.Equal(2, deleted);
        var remaining = _store.Query(0, 10_000);
        Assert.Equal(3000, Assert.Single(remaining).BucketUtcMs);
    }

    [Fact]
    public void MultipleComponents_AreIndependentInQuery()
    {
        _store.UpsertBuckets(new[]
        {
            Row("cpu", 1000, avg: 60),
            Row("gpu:0", 1000, avg: 70),
        });

        var rows = _store.Query(0, 10_000);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.ComponentId == "cpu" && r.AvgC == 60);
        Assert.Contains(rows, r => r.ComponentId == "gpu:0" && r.AvgC == 70);
    }

    private static TemperatureBucketRow GpuRow(string id, string name, long bucketUtcMs, double avg = 50, double max = 60, int samples = 10) =>
        new(id, "gpu", name, bucketUtcMs, avg, max, samples);

    [Fact]
    public void FindLegacyGpuComponentIds_ReturnsOnlyDigitSuffixIdsForThatName()
    {
        _store.UpsertBuckets(new[]
        {
            GpuRow("gpu:0", "NVIDIA GeForce RTX 5080", 1000),
            GpuRow("gpu:GPU-abc123", "NVIDIA GeForce RTX 5080", 1000),
            GpuRow("gpu:0", "AMD Radeon RX 7900", 2000),
        });

        var ids = _store.FindLegacyGpuComponentIds("NVIDIA GeForce RTX 5080");

        Assert.Equal(new[] { "gpu:0" }, ids);
    }

    [Fact]
    public void FindLegacyGpuComponentIds_ReturnsEmpty_WhenNoLegacyRows()
    {
        _store.UpsertBuckets(new[] { GpuRow("gpu:GPU-abc123", "NVIDIA GeForce RTX 5080", 1000) });

        Assert.Empty(_store.FindLegacyGpuComponentIds("NVIDIA GeForce RTX 5080"));
    }

    [Fact]
    public void FindLegacyGpuComponentIds_ReturnsMultiple_WhenAmbiguous()
    {
        _store.UpsertBuckets(new[]
        {
            GpuRow("gpu:0", "NVIDIA GeForce RTX 5080", 1000),
            GpuRow("gpu:1", "NVIDIA GeForce RTX 5080", 2000),
        });

        var ids = _store.FindLegacyGpuComponentIds("NVIDIA GeForce RTX 5080");

        Assert.Equal(2, ids.Count);
        Assert.Contains("gpu:0", ids);
        Assert.Contains("gpu:1", ids);
    }

    [Fact]
    public void RekeyComponent_MovesNonCollidingRows_AndRemovesOldId()
    {
        _store.UpsertBuckets(new[]
        {
            GpuRow("gpu:0", "NVIDIA GeForce RTX 5080", 1000, avg: 55),
            GpuRow("gpu:0", "NVIDIA GeForce RTX 5080", 2000, avg: 65),
        });

        var moved = _store.RekeyComponent("gpu:0", "gpu:GPU-abc123");

        Assert.Equal(2, moved);
        var rows = _store.Query(0, 10_000);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("gpu:GPU-abc123", r.ComponentId));
        Assert.Contains(rows, r => r.BucketUtcMs == 1000 && r.AvgC == 55);
        Assert.Contains(rows, r => r.BucketUtcMs == 2000 && r.AvgC == 65);
    }

    [Fact]
    public void RekeyComponent_Collision_KeepsRowWithMoreSamples()
    {
        _store.UpsertBuckets(new[] { GpuRow("gpu:0", "NVIDIA GeForce RTX 5080", 1000, avg: 55, samples: 20) });
        _store.UpsertBuckets(new[] { GpuRow("gpu:GPU-abc123", "NVIDIA GeForce RTX 5080", 1000, avg: 70, samples: 5) });

        var moved = _store.RekeyComponent("gpu:0", "gpu:GPU-abc123");

        Assert.Equal(1, moved);
        var row = Assert.Single(_store.Query(0, 10_000));
        Assert.Equal("gpu:GPU-abc123", row.ComponentId);
        Assert.Equal(55, row.AvgC);
        Assert.Equal(20, row.Samples);
    }

    [Fact]
    public void RekeyComponent_Collision_KeepsNewRow_WhenNewHasMoreOrEqualSamples()
    {
        _store.UpsertBuckets(new[] { GpuRow("gpu:0", "NVIDIA GeForce RTX 5080", 1000, avg: 55, samples: 5) });
        _store.UpsertBuckets(new[] { GpuRow("gpu:GPU-abc123", "NVIDIA GeForce RTX 5080", 1000, avg: 70, samples: 20) });

        _store.RekeyComponent("gpu:0", "gpu:GPU-abc123");

        var row = Assert.Single(_store.Query(0, 10_000));
        Assert.Equal("gpu:GPU-abc123", row.ComponentId);
        Assert.Equal(70, row.AvgC);
        Assert.Equal(20, row.Samples);
    }

    [Fact]
    public void RekeyComponent_NoRows_ReturnsZero()
    {
        Assert.Equal(0, _store.RekeyComponent("gpu:0", "gpu:GPU-abc123"));
    }
}
