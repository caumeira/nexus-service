using System;
using System.IO;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

public class FpsRingStoreTests : IDisposable
{
    private readonly string _dir;

    public FpsRingStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-fpsring-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private FpsRingStore CreateStore(long capacity = 100, long initialPruneFloorSec = long.MinValue) =>
        new(Path.Combine(_dir, "fps.ring"), capacity, initialPruneFloorSec);

    private static MetricSample Sample(long ts, int? fps) =>
        new(ts, null, null, null, null, null, Array.Empty<GpuReading>(), Array.Empty<FanReading>()) { Fps = fps };

    [Fact]
    public void Append_ThenQuery_RoundTrips()
    {
        using var store = CreateStore();
        store.Append(new[] { Sample(10, 144) });

        var row = Assert.Single(store.Query(0, 100));
        Assert.Equal(10, row.TsSec);
        Assert.Equal(144, row.Fps);
    }

    [Fact]
    public void Append_FpsZero_RoundTripsAsNull_UnlikeAScalarZero()
    {
        using var store = CreateStore();
        store.Append(new[] { Sample(10, 0) });

        var row = Assert.Single(store.Query(0, 100));
        Assert.Null(row.Fps);
    }

    [Fact]
    public void Append_NoFpsReading_RoundTripsAsNull()
    {
        using var store = CreateStore();
        store.Append(new[] { Sample(10, null) });

        var row = Assert.Single(store.Query(0, 100));
        Assert.Null(row.Fps);
    }

    [Fact]
    public void RaisePruneFloor_HidesOlderRows()
    {
        using var store = CreateStore();
        store.Append(new[] { Sample(1000, 60), Sample(5000, 120) });

        store.RaisePruneFloor(3000);

        var row = Assert.Single(store.Query(0, 10_000));
        Assert.Equal(5000, row.TsSec);
    }

    [Fact]
    public void QueryDecimatedRaw_AveragesAndMaxesWithinASlot()
    {
        using var store = CreateStore();
        store.Append(new[] { Sample(0, 60), Sample(1, 120) });

        var slot = Assert.Single(store.QueryDecimatedRaw(0, 1, stepSeconds: 10));

        Assert.Equal(90, slot.Avg);
        Assert.Equal(120, slot.Max);
    }

    [Fact]
    public void Clear_RemovesEveryRow()
    {
        using var store = CreateStore();
        store.Append(new[] { Sample(10, 144) });

        store.Clear();

        Assert.Empty(store.Query(0, 100));
    }
}
