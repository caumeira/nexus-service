using System;
using System.IO;
using System.Linq;
using Nexus.Service.Diagnostics.Temperature;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Temperature;

public class GpuComponentIdMigrationTests
{
    private static TemperatureBucketRow GpuRow(string id, string name, long bucketUtcMs, int samples = 10) =>
        new(id, "gpu", name, bucketUtcMs, 55, 65, samples);

    [Fact]
    public void Migrate_RekeysSingleLegacyId_ToResolvedUuid()
    {
        var store = new InMemoryTemperatureHistoryStore();
        store.UpsertBuckets(new[] { GpuRow("gpu:0", "NVIDIA GeForce RTX 5080", 1000) });

        GpuComponentIdMigration.Migrate(store, new[] { ("NVIDIA GeForce RTX 5080", "GPU-abc123") });

        var row = Assert.Single(store.Query(0, 10_000));
        Assert.Equal("gpu:GPU-abc123", row.ComponentId);
    }

    [Fact]
    public void Migrate_AmbiguousLegacyIds_LeavesRowsUntouched()
    {
        var store = new InMemoryTemperatureHistoryStore();
        store.UpsertBuckets(new[]
        {
            GpuRow("gpu:0", "NVIDIA GeForce RTX 5080", 1000),
            GpuRow("gpu:1", "NVIDIA GeForce RTX 5080", 2000),
        });

        GpuComponentIdMigration.Migrate(store, new[] { ("NVIDIA GeForce RTX 5080", "GPU-abc123") });

        var rows = store.Query(0, 10_000);
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.ComponentId == "gpu:GPU-abc123");
        Assert.Contains(rows, r => r.ComponentId == "gpu:0");
        Assert.Contains(rows, r => r.ComponentId == "gpu:1");
    }

    [Fact]
    public void Migrate_NoLegacyRows_IsNoOp()
    {
        var store = new InMemoryTemperatureHistoryStore();

        GpuComponentIdMigration.Migrate(store, new[] { ("NVIDIA GeForce RTX 5080", "GPU-abc123") });

        Assert.Empty(store.Query(0, long.MaxValue));
    }

    [Fact]
    public void Migrate_OnlyTouchesLegacyIdsMatchingResolvedGpuName()
    {
        var store = new InMemoryTemperatureHistoryStore();
        store.UpsertBuckets(new[]
        {
            GpuRow("gpu:0", "NVIDIA GeForce RTX 5080", 1000),
            GpuRow("gpu:1", "AMD Radeon RX 7900", 1000),
        });

        GpuComponentIdMigration.Migrate(store, new[] { ("NVIDIA GeForce RTX 5080", "GPU-abc123") });

        var rows = store.Query(0, 10_000);
        Assert.Contains(rows, r => r.ComponentId == "gpu:GPU-abc123" && r.Name == "NVIDIA GeForce RTX 5080");
        Assert.Contains(rows, r => r.ComponentId == "gpu:1" && r.Name == "AMD Radeon RX 7900");
    }

    [Fact]
    public void Migrate_MultipleGpus_RekeysEachIndependently()
    {
        var store = new InMemoryTemperatureHistoryStore();
        store.UpsertBuckets(new[]
        {
            GpuRow("gpu:0", "NVIDIA GeForce RTX 5080", 1000),
            GpuRow("gpu:1", "AMD Radeon RX 7900", 1000),
        });

        GpuComponentIdMigration.Migrate(store, new[]
        {
            ("NVIDIA GeForce RTX 5080", "GPU-abc123"),
            ("AMD Radeon RX 7900", "GPU-def456"),
        });

        var ids = store.Query(0, 10_000).Select(r => r.ComponentId).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "gpu:GPU-abc123", "gpu:GPU-def456" }, ids);
    }

    [Fact]
    public void Migrate_AgainstSqliteStore_RekeysSingleLegacyId()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-temphistory-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        using var store = new SqliteTemperatureHistoryStore(Path.Combine(dir, "temperature.db"));
        try
        {
            store.UpsertBuckets(new[] { GpuRow("gpu:0", "NVIDIA GeForce RTX 5080", 1000) });

            GpuComponentIdMigration.Migrate(store, new[] { ("NVIDIA GeForce RTX 5080", "GPU-abc123") });

            var row = Assert.Single(store.Query(0, 10_000));
            Assert.Equal("gpu:GPU-abc123", row.ComponentId);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
