using System;
using System.IO;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// The raw (step &lt; 60, no minute rollup involved) QueryGpuDecimated/
/// QueryFanDecimated behavior every store must have: per-gpu/per-fan avg/max
/// within a slot, keyed by id, and an empty result when no gpu/fan data
/// falls in the window. Run against both SqliteMetricsHistoryStore
/// (SqliteGpuFanDecimatedRawSpecTests) and BinaryMetricsHistoryStore
/// (BinaryGpuFanDecimatedRawSpecTests) - the rollup-eligible (step&gt;=60)
/// gpu/fan cases live in RollupHistorySpec, and component-temp decimation
/// stays SQLite-only in DecimatedHistoryStoreTests since that series is not
/// persisted by the binary store yet (Phase 3).
/// </summary>
public abstract class GpuFanDecimatedRawSpec : IDisposable
{
    private readonly string _dir;
    protected readonly IMetricsHistoryStore Store;

    protected GpuFanDecimatedRawSpec()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-gpufandecimated-spec-" + Guid.NewGuid().ToString("N")[..8]);
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

    [Fact]
    public void QueryGpuDecimated_AveragesAndMaxesPerGpu_KeyedById()
    {
        var s1 = new MetricSample(0, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 10, 40), new GpuReading("gpu-1", "RX 7900", "", 50, 60) },
            Array.Empty<FanReading>());
        var s2 = new MetricSample(1, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 30, 42) },
            Array.Empty<FanReading>());
        Store.Append(new[] { s1, s2 }, null);

        var slots = Store.QueryGpuDecimated(0, 1, stepSeconds: 10);

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
        Store.Append(new[] { s1, s2 }, null);

        var slot = Assert.Single(Store.QueryFanDecimated(0, 1, stepSeconds: 10));

        Assert.Equal("fan-0", slot.FanId);
        Assert.Equal(1100, slot.RpmAvg);
        Assert.Equal(1200, slot.RpmMax);
        Assert.Equal(45, slot.DutyAvg);
    }

    [Fact]
    public void QueryGpuDecimated_ReturnsEmpty_WhenNoGpuDataInWindow()
    {
        Store.Append(new[] { Scalars(0, cpu: 10) }, null);

        Assert.Empty(Store.QueryGpuDecimated(0, 0, stepSeconds: 10));
    }

    [Fact]
    public void QueryGpuDecimated_IncludesASlot_WhenBothFieldsAreNull_ButTheRowExisted()
    {
        // A GPU whose sensor lookup fails for both load and temp still gets
        // a row every tick (InsertGpuReadings/GpuRingStore.Append both write
        // unconditionally) - the slot must still appear, with null avg/max,
        // rather than vanish because neither field had a value to average.
        var s = new MetricSample(0, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", null, null) },
            Array.Empty<FanReading>());
        Store.Append(new[] { s }, null);

        var slot = Assert.Single(Store.QueryGpuDecimated(0, 0, stepSeconds: 10));

        Assert.Equal("gpu-0", slot.GpuId);
        Assert.Null(slot.LoadAvg);
        Assert.Null(slot.TempAvg);
    }
}

public sealed class SqliteGpuFanDecimatedRawSpecTests : GpuFanDecimatedRawSpec
{
    protected override IMetricsHistoryStore CreateStore(string dir) =>
        new SqliteMetricsHistoryStore(Path.Combine(dir, "metrics.db"));
}

public sealed class BinaryGpuFanDecimatedRawSpecTests : GpuFanDecimatedRawSpec
{
    protected override IMetricsHistoryStore CreateStore(string dir) =>
        new BinaryMetricsHistoryStore(dir);
}
