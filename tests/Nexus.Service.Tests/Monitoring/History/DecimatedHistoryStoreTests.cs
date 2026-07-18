using System;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

public class DecimatedHistoryStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteMetricsHistoryStore _store;

    public DecimatedHistoryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-decimatedhistory-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _store = new SqliteMetricsHistoryStore(Path.Combine(_dir, "metrics.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static MetricSample Scalars(long ts, double? cpu, double? mem = 10, double? netIn = 100, double? netOut = 50, double? cpuTemp = 40) =>
        new(ts, cpu, mem, netIn, netOut, cpuTemp, Array.Empty<GpuReading>(), Array.Empty<FanReading>());

    // The raw (step<60) scalar decimation cases are pinned once in
    // ScalarDecimatedRawSpec and run against both this store and
    // BinaryMetricsHistoryStore - see that file. GPU/fan/component-temp
    // decimation below stays SQLite-only: BinaryMetricsHistoryStore does not
    // persist those series yet (see its class doc).

    [Fact]
    public void QueryGpuDecimated_AveragesAndMaxesPerGpu_KeyedById()
    {
        var s1 = new MetricSample(0, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 10, 40), new GpuReading("gpu-1", "RX 7900", "", 50, 60) },
            Array.Empty<FanReading>());
        var s2 = new MetricSample(1, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 30, 42) },
            Array.Empty<FanReading>());
        _store.Append(new[] { s1, s2 }, null);

        var slots = _store.QueryGpuDecimated(0, 1, stepSeconds: 10);

        var gpu0 = Assert.Single(slots, s => s.GpuId == "gpu-0");
        Assert.Equal("RTX 5080", gpu0.Name);
        Assert.Equal(20, gpu0.LoadAvg);
        Assert.Equal(30, gpu0.LoadMax);
        Assert.Equal(41, gpu0.TempAvg);

        var gpu1 = Assert.Single(slots, s => s.GpuId == "gpu-1");
        Assert.Equal(50, gpu1.LoadAvg);
    }

    [Fact]
    public void QueryFanDecimated_AveragesAndMaxesPerFan_KeyedById_NotX10Scaled()
    {
        var s1 = new MetricSample(0, null, null, null, null, null,
            Array.Empty<GpuReading>(), new[] { new FanReading("fan-0", "Fan 1", 1000, 40) });
        var s2 = new MetricSample(1, null, null, null, null, null,
            Array.Empty<GpuReading>(), new[] { new FanReading("fan-0", "Fan 1", 1200, 50) });
        _store.Append(new[] { s1, s2 }, null);

        var slot = Assert.Single(_store.QueryFanDecimated(0, 1, stepSeconds: 10));

        Assert.Equal("fan-0", slot.FanId);
        Assert.Equal(1100, slot.RpmAvg);
        Assert.Equal(1200, slot.RpmMax);
        Assert.Equal(45, slot.DutyAvg);
    }

    [Fact]
    public void QueryGpuDecimated_ReturnsEmpty_WhenNoGpuDataInWindow()
    {
        _store.Append(new[] { Scalars(0, cpu: 10) }, null);

        Assert.Empty(_store.QueryGpuDecimated(0, 0, stepSeconds: 10));
    }

    private static MetricSample ComponentSample(long ts, params ComponentTempReading[] components) =>
        new(ts, null, null, null, null, null, Array.Empty<GpuReading>(), Array.Empty<FanReading>()) { ComponentTemps = components };

    [Fact]
    public void QueryComponentTempDecimated_AveragesAndMaxesPerComponent_KeyedById()
    {
        var s1 = ComponentSample(0,
            new ComponentTempReading("ram:0", "ram", "DIMM A2", 40),
            new ComponentTempReading("storage:ABC", "storage", "Samsung 990 Pro", 35));
        var s2 = ComponentSample(1, new ComponentTempReading("ram:0", "ram", "DIMM A2", 44));
        _store.Append(new[] { s1, s2 }, null);

        var slots = _store.QueryComponentTempDecimated(0, 1, stepSeconds: 10);

        var ram = Assert.Single(slots, s => s.ComponentId == "ram:0");
        Assert.Equal("ram", ram.Kind);
        Assert.Equal("DIMM A2", ram.Name);
        Assert.Equal(42, ram.Avg);
        Assert.Equal(44, ram.Max);

        var storage = Assert.Single(slots, s => s.ComponentId == "storage:ABC");
        Assert.Equal("storage", storage.Kind);
        Assert.Equal("Samsung 990 Pro", storage.Name);
        Assert.Equal(35, storage.Avg);
    }

    [Fact]
    public void QueryComponentTempDecimated_SplitsRowsAcrossSlotBoundaries()
    {
        var s1 = ComponentSample(0, new ComponentTempReading("ram:0", "ram", "DIMM A2", 40));
        var s2 = ComponentSample(10, new ComponentTempReading("ram:0", "ram", "DIMM A2", 60));
        _store.Append(new[] { s1, s2 }, null);

        var slots = _store.QueryComponentTempDecimated(0, 19, stepSeconds: 10).OrderBy(s => s.Slot).ToList();

        Assert.Equal(2, slots.Count);
        Assert.Equal(0, slots[0].Slot);
        Assert.Equal(40, slots[0].Avg);
        Assert.Equal(10, slots[1].Slot);
        Assert.Equal(60, slots[1].Avg);
    }

    [Fact]
    public void QueryComponentTempDecimated_ReturnsEmpty_WhenNoComponentDataInWindow()
    {
        _store.Append(new[] { Scalars(0, cpu: 10) }, null);

        Assert.Empty(_store.QueryComponentTempDecimated(0, 0, stepSeconds: 10));
    }
}
