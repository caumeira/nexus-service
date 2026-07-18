using System;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// The raw (step &lt; 60, no minute rollup involved) QueryScalarsDecimated
/// behavior the store must have: per-field avg/max within a slot, slot
/// boundaries, an all-null slot staying present with a null field, and exact
/// agreement with the pure MetricsDecimation.Decimate function. Runs against
/// BinaryMetricsHistoryStore (BinaryScalarDecimatedRawSpecTests) - the
/// GPU/fan raw cases live in GpuFanDecimatedRawSpec and the component-temp
/// raw cases in ComponentTempDecimatedRawSpec.
/// </summary>
public abstract class ScalarDecimatedRawSpec : IDisposable
{
    private readonly string _dir;
    protected readonly IMetricsHistoryStore Store;

    protected ScalarDecimatedRawSpec()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-decimatedhistory-spec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        Store = CreateStore(_dir);
    }

    protected abstract IMetricsHistoryStore CreateStore(string dir);

    public virtual void Dispose()
    {
        Store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static MetricSample Scalars(
        long ts, double? cpu, double? mem = 10, double? netIn = 100, double? netOut = 50, double? cpuTemp = 40,
        double? diskRead = null, double? diskWrite = null) =>
        new(ts, cpu, mem, netIn, netOut, cpuTemp, Array.Empty<GpuReading>(), Array.Empty<FanReading>(),
            DiskReadBytesPerSec: diskRead, DiskWriteBytesPerSec: diskWrite);

    [Fact]
    public void QueryScalarsDecimated_AveragesAndMaxesEachFieldWithinASlot()
    {
        Store.Append(new[] { Scalars(0, cpu: 10), Scalars(1, cpu: 30) }, null);

        var slots = Store.QueryScalarsDecimated(0, 1, stepSeconds: 10);

        var slot = Assert.Single(slots);
        Assert.Equal(0, slot.Slot);
        Assert.Equal(20, slot.CpuAvg);
        Assert.Equal(30, slot.CpuMax);
    }

    [Fact]
    public void QueryScalarsDecimated_SplitsRowsAcrossSlotBoundaries()
    {
        Store.Append(new[] { Scalars(0, cpu: 10), Scalars(10, cpu: 50) }, null);

        var slots = Store.QueryScalarsDecimated(0, 19, stepSeconds: 10).OrderBy(s => s.Slot).ToList();

        Assert.Equal(2, slots.Count);
        Assert.Equal(0, slots[0].Slot);
        Assert.Equal(10, slots[0].CpuAvg);
        Assert.Equal(10, slots[1].Slot);
        Assert.Equal(50, slots[1].CpuAvg);
    }

    [Fact]
    public void QueryScalarsDecimated_LeavesAFieldNull_WhenEveryReadingInTheSlotWasNull()
    {
        Store.Append(new[] { Scalars(0, cpu: null) }, null);

        var slot = Assert.Single(Store.QueryScalarsDecimated(0, 0, stepSeconds: 10));

        Assert.Null(slot.CpuAvg);
        Assert.Null(slot.CpuMax);
    }

    [Fact]
    public void QueryScalarsDecimated_MatchesRawDecimation_ForAWiderSyntheticWindow()
    {
        var samples = Enumerable.Range(0, 25).Select(i => Scalars(i, cpu: i)).ToArray();
        Store.Append(samples, null);

        var decimated = Store.QueryScalarsDecimated(0, 24, stepSeconds: 10).OrderBy(s => s.Slot).ToList();
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
    public void QueryScalarsDecimated_AveragesAndMaxesDiskFieldsWithinASlot()
    {
        Store.Append(new[]
        {
            Scalars(0, cpu: 10, diskRead: 1000, diskWrite: 200),
            Scalars(1, cpu: 10, diskRead: 3000, diskWrite: 600),
        }, null);

        var slot = Assert.Single(Store.QueryScalarsDecimated(0, 1, stepSeconds: 10));

        Assert.Equal(2000, slot.DiskReadAvg);
        Assert.Equal(3000, slot.DiskReadMax);
        Assert.Equal(400, slot.DiskWriteAvg);
        Assert.Equal(600, slot.DiskWriteMax);
    }

    [Fact]
    public void QueryScalarsDecimated_LeavesDiskFieldsNull_WhenEveryReadingInTheSlotWasNull()
    {
        Store.Append(new[] { Scalars(0, cpu: 10, diskRead: null, diskWrite: null) }, null);

        var slot = Assert.Single(Store.QueryScalarsDecimated(0, 0, stepSeconds: 10));

        Assert.Null(slot.DiskReadAvg);
        Assert.Null(slot.DiskReadMax);
        Assert.Null(slot.DiskWriteAvg);
        Assert.Null(slot.DiskWriteMax);
    }
}

public sealed class BinaryScalarDecimatedRawSpecTests : ScalarDecimatedRawSpec
{
    protected override IMetricsHistoryStore CreateStore(string dir) =>
        new BinaryMetricsHistoryStore(dir);
}
