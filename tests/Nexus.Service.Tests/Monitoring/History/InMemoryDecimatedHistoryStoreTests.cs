using System;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

public class InMemoryDecimatedHistoryStoreTests
{
    private static MetricSample Scalars(long ts, double? cpu, double? diskRead = null, double? diskWrite = null) =>
        new(ts, cpu, null, null, null, null, Array.Empty<GpuReading>(), Array.Empty<FanReading>(),
            DiskReadBytesPerSec: diskRead, DiskWriteBytesPerSec: diskWrite);

    [Fact]
    public void QueryScalarsDecimated_MatchesTheSqliteStoresShape_ForTheSameData()
    {
        var store = new InMemoryMetricsHistoryStore();
        store.Append(new[] { Scalars(0, cpu: 10), Scalars(1, cpu: 30) }, null);

        var slot = Assert.Single(store.QueryScalarsDecimated(0, 1, stepSeconds: 10));

        Assert.Equal(0, slot.Slot);
        Assert.Equal(20, slot.CpuAvg);
        Assert.Equal(30, slot.CpuMax);
    }

    [Fact]
    public void QueryScalarsDecimated_AveragesAndMaxesDiskFields()
    {
        var store = new InMemoryMetricsHistoryStore();
        store.Append(new[] { Scalars(0, cpu: 10, diskRead: 1000, diskWrite: 200), Scalars(1, cpu: 30, diskRead: 3000, diskWrite: 600) }, null);

        var slot = Assert.Single(store.QueryScalarsDecimated(0, 1, stepSeconds: 10));

        Assert.Equal(2000, slot.DiskReadAvg);
        Assert.Equal(3000, slot.DiskReadMax);
        Assert.Equal(400, slot.DiskWriteAvg);
        Assert.Equal(600, slot.DiskWriteMax);
    }

    [Fact]
    public void QueryGpuDecimated_AveragesPerGpu()
    {
        var store = new InMemoryMetricsHistoryStore();
        var s1 = new MetricSample(0, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 10, 40) }, Array.Empty<FanReading>());
        var s2 = new MetricSample(1, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 30, 42) }, Array.Empty<FanReading>());
        store.Append(new[] { s1, s2 }, null);

        var slot = Assert.Single(store.QueryGpuDecimated(0, 1, stepSeconds: 10));

        Assert.Equal("gpu-0", slot.GpuId);
        Assert.Equal(20, slot.LoadAvg);
        Assert.Equal(30, slot.LoadMax);
    }

    [Fact]
    public void QueryFanDecimated_AveragesPerFan()
    {
        var store = new InMemoryMetricsHistoryStore();
        var s1 = new MetricSample(0, null, null, null, null, null,
            Array.Empty<GpuReading>(), new[] { new FanReading("fan-0", "Fan 1", 1000, 40) });
        var s2 = new MetricSample(1, null, null, null, null, null,
            Array.Empty<GpuReading>(), new[] { new FanReading("fan-0", "Fan 1", 1200, 50) });
        store.Append(new[] { s1, s2 }, null);

        var slot = Assert.Single(store.QueryFanDecimated(0, 1, stepSeconds: 10));

        Assert.Equal(1100, slot.RpmAvg);
        Assert.Equal(45, slot.DutyAvg);
    }

    [Fact]
    public void QueryComponentTempDecimated_AveragesPerComponent()
    {
        var store = new InMemoryMetricsHistoryStore();
        var s1 = new MetricSample(0, null, null, null, null, null, Array.Empty<GpuReading>(), Array.Empty<FanReading>())
        {
            ComponentTemps = new[] { new ComponentTempReading("ram:0", "ram", "DIMM A2", 40) },
        };
        var s2 = new MetricSample(1, null, null, null, null, null, Array.Empty<GpuReading>(), Array.Empty<FanReading>())
        {
            ComponentTemps = new[] { new ComponentTempReading("ram:0", "ram", "DIMM A2", 44) },
        };
        store.Append(new[] { s1, s2 }, null);

        var slot = Assert.Single(store.QueryComponentTempDecimated(0, 1, stepSeconds: 10));

        Assert.Equal("ram:0", slot.ComponentId);
        Assert.Equal("ram", slot.Kind);
        Assert.Equal("DIMM A2", slot.Name);
        Assert.Equal(42, slot.Avg);
        Assert.Equal(44, slot.Max);
    }
}
