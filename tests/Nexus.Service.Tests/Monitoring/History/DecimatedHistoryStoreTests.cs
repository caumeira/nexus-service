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

    [Fact]
    public void QueryScalarsDecimated_AveragesAndMaxesEachFieldWithinASlot()
    {
        _store.Append(new[] { Scalars(0, cpu: 10), Scalars(1, cpu: 30) }, null);

        var slots = _store.QueryScalarsDecimated(0, 1, stepSeconds: 10);

        var slot = Assert.Single(slots);
        Assert.Equal(0, slot.Slot);
        Assert.Equal(20, slot.CpuAvg);
        Assert.Equal(30, slot.CpuMax);
    }

    [Fact]
    public void QueryScalarsDecimated_SplitsRowsAcrossSlotBoundaries()
    {
        _store.Append(new[] { Scalars(0, cpu: 10), Scalars(10, cpu: 50) }, null);

        var slots = _store.QueryScalarsDecimated(0, 19, stepSeconds: 10).OrderBy(s => s.Slot).ToList();

        Assert.Equal(2, slots.Count);
        Assert.Equal(0, slots[0].Slot);
        Assert.Equal(10, slots[0].CpuAvg);
        Assert.Equal(10, slots[1].Slot);
        Assert.Equal(50, slots[1].CpuAvg);
    }

    [Fact]
    public void QueryScalarsDecimated_LeavesAFieldNull_WhenEveryReadingInTheSlotWasNull()
    {
        _store.Append(new[] { Scalars(0, cpu: null) }, null);

        var slot = Assert.Single(_store.QueryScalarsDecimated(0, 0, stepSeconds: 10));

        Assert.Null(slot.CpuAvg);
        Assert.Null(slot.CpuMax);
    }

    [Fact]
    public void QueryScalarsDecimated_MatchesRawDecimation_ForAWiderSyntheticWindow()
    {
        var samples = Enumerable.Range(0, 25).Select(i => Scalars(i, cpu: i)).ToArray();
        _store.Append(samples, null);

        var decimated = _store.QueryScalarsDecimated(0, 24, stepSeconds: 10).OrderBy(s => s.Slot).ToList();
        var raw = MetricsDecimation.Decimate(
            samples.Select(s => new MetricSamplePoint(s.TsSec, s.CpuPercent)), 0, 24, 10);

        Assert.Equal(raw.Count, decimated.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            Assert.Equal(raw[i].T, decimated[i].Slot);
            Assert.Equal(raw[i].Avg, decimated[i].CpuAvg!.Value, precision: 6);
            Assert.Equal(raw[i].Max, decimated[i].CpuMax!.Value, precision: 6);
        }
    }

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
}
