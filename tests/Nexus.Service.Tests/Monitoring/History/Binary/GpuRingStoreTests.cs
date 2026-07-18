using System;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

/// <summary>
/// GpuRingStore-level coverage for behavior the shared IMetricsHistoryStore
/// specs (which exercise it only indirectly, through BinaryMetricsHistoryStore
/// at the real 7-day/32-entity capacity) cannot easily reach: a small,
/// test-chosen per-second ring capacity so wraparound is cheap to construct,
/// a new GPU growing the entity pool by one ring-file pair, the
/// entityCapacity bound, and the minute-rollup rebuild's replay-dedup at the
/// entity level. FanRingStoreTests covers the same shape for fans; this file
/// is the representative for the entity-ring wraparound case both kinds
/// share.
/// </summary>
public class GpuRingStoreTests : IDisposable
{
    private readonly string _dir;

    public GpuRingStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-gpuring-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private GpuRingStore CreateStore(long secondCapacity = 1000, long minuteCapacity = 100, int entityCapacity = 32) =>
        new(_dir, secondCapacity, minuteCapacity, entityCapacity, initialPruneFloorSec: long.MinValue);

    private static MetricSample GpuSample(long ts, params GpuReading[] gpus) =>
        new(ts, null, null, null, null, null, gpus, Array.Empty<FanReading>());

    [Fact]
    public void Append_ThenQuery_RoundTripsLoadAndTemp()
    {
        using var store = CreateStore();
        store.Append(new[] { GpuSample(10, new GpuReading("gpu-0", "RTX 5080", "", 42.3, 55.1)) });

        var byTs = store.Query(0, 100);

        var reading = Assert.Single(Assert.Single(byTs).Value);
        Assert.Equal("gpu-0", reading.GpuId);
        Assert.Equal("RTX 5080", reading.Name);
        Assert.Equal(42.3, reading.LoadPercent);
        Assert.Equal(55.1, reading.TempC);
    }

    [Fact]
    public void Append_ANewGpu_ExtendsThePool_WithItsOwnRingFiles()
    {
        using var store = CreateStore();
        store.Append(new[] { GpuSample(0, new GpuReading("gpu-0", "A", "", 10, 40)) });
        store.Append(new[] { GpuSample(0, new GpuReading("gpu-1", "B", "", 20, 50)) });

        Assert.True(File.Exists(Path.Combine(_dir, "0.ring")));
        Assert.True(File.Exists(Path.Combine(_dir, "1.ring")));

        var readings = store.Query(0, 0)[0];
        Assert.Contains(readings, r => r.GpuId == "gpu-0" && r.LoadPercent == 10);
        Assert.Contains(readings, r => r.GpuId == "gpu-1" && r.LoadPercent == 20);
    }

    [Fact]
    public void Append_TwoNewGpusDiscoveredInTheSameBatch_BothGetDistinctRingIndices()
    {
        using var store = CreateStore();
        store.Append(new[]
        {
            GpuSample(0, new GpuReading("gpu-0", "A", "", 10, 40), new GpuReading("gpu-1", "B", "", 20, 50)),
        });

        Assert.True(File.Exists(Path.Combine(_dir, "0.ring")));
        Assert.True(File.Exists(Path.Combine(_dir, "1.ring")));

        var readings = store.Query(0, 0)[0];
        Assert.Contains(readings, r => r.GpuId == "gpu-0" && r.LoadPercent == 10);
        Assert.Contains(readings, r => r.GpuId == "gpu-1" && r.LoadPercent == 20);
    }

    [Fact]
    public void Append_PastEntityCapacity_SilentlyDropsTheExtraGpu()
    {
        using var store = CreateStore(entityCapacity: 1);
        store.Append(new[] { GpuSample(0, new GpuReading("gpu-0", "A", "", 10, 40)) });
        store.Append(new[] { GpuSample(0, new GpuReading("gpu-1", "B", "", 20, 50)) });

        var readings = store.Query(0, 0)[0];
        Assert.Single(readings);
        Assert.Equal("gpu-0", readings[0].GpuId);
    }

    [Fact]
    public void SecondRing_WrapsPastCapacity_OldTimestampReadsAsAbsent()
    {
        using var store = CreateStore(secondCapacity: 10);
        store.Append(new[] { GpuSample(5, new GpuReading("gpu-0", "A", "", 10, 40)) });
        store.Append(new[] { GpuSample(15, new GpuReading("gpu-0", "A", "", 20, 50)) }); // 15 mod 10 == 5 mod 10

        var byTs = store.Query(0, 100);

        Assert.False(byTs.ContainsKey(5));
        var reading = Assert.Single(byTs[15]);
        Assert.Equal(20, reading.LoadPercent);
    }

    [Fact]
    public void QueryRollupDecimated_RebuildReflectsAReplayedSecond_NotDoubleCounted()
    {
        using var store = CreateStore();
        store.Append(new[] { GpuSample(0, new GpuReading("gpu-0", "A", "", 10, 40)) });
        store.Append(new[] { GpuSample(30, new GpuReading("gpu-0", "A", "", 50, 44)) });
        store.Append(new[] { GpuSample(30, new GpuReading("gpu-0", "A", "", 50, 44)) }); // ts=30 replayed, unchanged

        var slot = Assert.Single(store.QueryRollupDecimated(0, 59, stepSeconds: 60));

        Assert.Equal(30, slot.LoadAvg); // (10+50)/2, not (10+50+50)/3
        Assert.Equal(50, slot.LoadMax);
    }

    [Fact]
    public void QueryRawDecimated_AveragesWithinASlot()
    {
        using var store = CreateStore();
        store.Append(new[]
        {
            GpuSample(0, new GpuReading("gpu-0", "A", "", 10, 40)),
            GpuSample(1, new GpuReading("gpu-0", "A", "", 30, 42)),
        });

        var slot = Assert.Single(store.QueryRawDecimated(0, 1, stepSeconds: 10));

        Assert.Equal(20, slot.LoadAvg);
        Assert.Equal(30, slot.LoadMax);
    }
}
