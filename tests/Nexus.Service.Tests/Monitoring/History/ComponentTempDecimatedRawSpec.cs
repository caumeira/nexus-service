using System;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// The raw (step &lt; 60) QueryComponentTempDecimated behavior the store must
/// have: per-component avg/max within a slot, keyed by component id, and
/// slot boundaries. The step&gt;=60 rollup-eligible path is covered by
/// RollupHistorySpec instead. Runs against BinaryMetricsHistoryStore
/// (BinaryComponentTempDecimatedRawSpecTests).
/// </summary>
public abstract class ComponentTempDecimatedRawSpec : IDisposable
{
    private readonly string _dir;
    protected readonly IMetricsHistoryStore Store;

    protected ComponentTempDecimatedRawSpec()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-componenttempdecimated-spec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        Store = CreateStore(_dir);
    }

    protected abstract IMetricsHistoryStore CreateStore(string dir);

    public virtual void Dispose()
    {
        Store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static MetricSample Scalars(long ts, double? cpu) =>
        new(ts, cpu, 10, 100, 50, 40, Array.Empty<GpuReading>(), Array.Empty<FanReading>());

    private static MetricSample ComponentSample(long ts, params ComponentTempReading[] components) =>
        new(ts, null, null, null, null, null, Array.Empty<GpuReading>(), Array.Empty<FanReading>()) { ComponentTemps = components };

    [Fact]
    public void QueryComponentTempDecimated_AveragesAndMaxesPerComponent_KeyedById()
    {
        var s1 = ComponentSample(0,
            new ComponentTempReading("ram:0", "ram", "DIMM A2", 40),
            new ComponentTempReading("storage:ABC", "storage", "Samsung 990 Pro", 35));
        var s2 = ComponentSample(1, new ComponentTempReading("ram:0", "ram", "DIMM A2", 44));
        Store.Append(new[] { s1, s2 }, null);

        var slots = Store.QueryComponentTempDecimated(0, 1, stepSeconds: 10);

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
        Store.Append(new[] { s1, s2 }, null);

        var slots = Store.QueryComponentTempDecimated(0, 19, stepSeconds: 10).OrderBy(s => s.Slot).ToList();

        Assert.Equal(2, slots.Count);
        Assert.Equal(0, slots[0].Slot);
        Assert.Equal(40, slots[0].Avg);
        Assert.Equal(10, slots[1].Slot);
        Assert.Equal(60, slots[1].Avg);
    }

    [Fact]
    public void QueryComponentTempDecimated_ReturnsEmpty_WhenNoComponentDataInWindow()
    {
        Store.Append(new[] { Scalars(0, cpu: 10) }, null);

        Assert.Empty(Store.QueryComponentTempDecimated(0, 0, stepSeconds: 10));
    }
}

public sealed class BinaryComponentTempDecimatedRawSpecTests : ComponentTempDecimatedRawSpec
{
    protected override IMetricsHistoryStore CreateStore(string dir) =>
        new BinaryMetricsHistoryStore(dir);
}
