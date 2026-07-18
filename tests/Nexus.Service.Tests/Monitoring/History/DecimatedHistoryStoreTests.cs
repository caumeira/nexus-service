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
    // ScalarDecimatedRawSpec, and the raw gpu/fan cases in
    // GpuFanDecimatedRawSpec - both run against this store and
    // BinaryMetricsHistoryStore. Component-temp decimation below stays
    // SQLite-only: BinaryMetricsHistoryStore does not persist that series
    // yet (Phase 3, see its class doc).

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
