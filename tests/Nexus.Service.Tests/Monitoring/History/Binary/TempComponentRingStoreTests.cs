using System;
using System.IO;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

/// <summary>
/// TempComponentRingStore-level coverage mirroring GpuRingStoreTests: a
/// small, test-chosen per-second ring capacity so wraparound is cheap to
/// construct, a new component growing the entity pool by one ring file, the
/// entityCapacity bound, and raw decimated averaging.
/// </summary>
public class TempComponentRingStoreTests : IDisposable
{
    private readonly string _dir;

    public TempComponentRingStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-tempcomponentring-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private TempComponentRingStore CreateStore(long secondCapacity = 1000, int entityCapacity = 32) =>
        new(_dir, secondCapacity, entityCapacity, initialPruneFloorSec: long.MinValue);

    private static MetricSample ComponentSample(long ts, params ComponentTempReading[] components) =>
        new(ts, null, null, null, null, null, Array.Empty<GpuReading>(), Array.Empty<FanReading>()) { ComponentTemps = components };

    [Fact]
    public void Append_ThenQuery_RoundTripsValue()
    {
        using var store = CreateStore();
        store.Append(new[] { ComponentSample(10, new ComponentTempReading("ram:0", "ram", "DIMM A2", 42.3)) });

        var byTs = store.Query(0, 100);

        var reading = Assert.Single(Assert.Single(byTs).Value);
        Assert.Equal("ram:0", reading.ComponentId);
        Assert.Equal("ram", reading.Kind);
        Assert.Equal("DIMM A2", reading.Name);
        Assert.Equal(42.3, reading.ValueC);
    }

    [Fact]
    public void Append_ANewComponent_ExtendsThePool_WithItsOwnRingFile()
    {
        using var store = CreateStore();
        store.Append(new[] { ComponentSample(0, new ComponentTempReading("ram:0", "ram", "A", 40)) });
        store.Append(new[] { ComponentSample(0, new ComponentTempReading("storage:serial1", "storage", "B", 35)) });

        Assert.True(File.Exists(Path.Combine(_dir, "0.ring")));
        Assert.True(File.Exists(Path.Combine(_dir, "1.ring")));

        var readings = store.Query(0, 0)[0];
        Assert.Contains(readings, r => r.ComponentId == "ram:0" && r.ValueC == 40);
        Assert.Contains(readings, r => r.ComponentId == "storage:serial1" && r.ValueC == 35);
    }

    [Fact]
    public void Append_PastEntityCapacity_SilentlyDropsTheExtraComponent()
    {
        using var store = CreateStore(entityCapacity: 1);
        store.Append(new[] { ComponentSample(0, new ComponentTempReading("ram:0", "ram", "A", 40)) });
        store.Append(new[] { ComponentSample(0, new ComponentTempReading("ram:1", "ram", "B", 50)) });

        var readings = store.Query(0, 0)[0];
        Assert.Single(readings);
        Assert.Equal("ram:0", readings[0].ComponentId);
    }

    [Fact]
    public void SecondRing_WrapsPastCapacity_OldTimestampReadsAsAbsent()
    {
        using var store = CreateStore(secondCapacity: 10);
        store.Append(new[] { ComponentSample(5, new ComponentTempReading("ram:0", "ram", "A", 40)) });
        store.Append(new[] { ComponentSample(15, new ComponentTempReading("ram:0", "ram", "A", 50)) }); // 15 mod 10 == 5 mod 10

        var byTs = store.Query(0, 100);

        Assert.False(byTs.ContainsKey(5));
        var reading = Assert.Single(byTs[15]);
        Assert.Equal(50, reading.ValueC);
    }

    [Fact]
    public void QueryRawDecimated_AveragesWithinASlot()
    {
        using var store = CreateStore();
        store.Append(new[]
        {
            ComponentSample(0, new ComponentTempReading("ram:0", "ram", "A", 40)),
            ComponentSample(1, new ComponentTempReading("ram:0", "ram", "A", 60)),
        });

        var slot = Assert.Single(store.QueryRawDecimated(0, 1, stepSeconds: 10));

        Assert.Equal(50, slot.Avg);
        Assert.Equal(60, slot.Max);
    }

    [Fact]
    public void QueryRawDecimated_ReturnsEmpty_WhenNoComponentDataInWindow()
    {
        using var store = CreateStore();
        store.Append(new[] { ComponentSample(0, new ComponentTempReading("ram:0", "ram", "A", 40)) });

        Assert.Empty(store.QueryRawDecimated(100, 200, stepSeconds: 10));
    }
}
