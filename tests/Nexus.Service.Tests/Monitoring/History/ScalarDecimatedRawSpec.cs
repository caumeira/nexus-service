using System;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// The raw (step &lt; 60, no minute rollup involved) QueryScalarsDecimated
/// behavior every store must have: per-field avg/max within a slot, slot
/// boundaries, an all-null slot staying present with a null field, and exact
/// agreement with the pure MetricsDecimation.Decimate function. Run against
/// both SqliteMetricsHistoryStore (SqliteScalarDecimatedRawSpecTests) and
/// BinaryMetricsHistoryStore (BinaryScalarDecimatedRawSpecTests) - the
/// GPU/fan raw cases live in GpuFanDecimatedRawSpec and the component-temp
/// raw cases in ComponentTempDecimatedRawSpec, each parameterized the same
/// way.
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

    private static MetricSample Scalars(long ts, double? cpu, double? mem = 10, double? netIn = 100, double? netOut = 50, double? cpuTemp = 40) =>
        new(ts, cpu, mem, netIn, netOut, cpuTemp, Array.Empty<GpuReading>(), Array.Empty<FanReading>());

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
}

public sealed class SqliteScalarDecimatedRawSpecTests : ScalarDecimatedRawSpec
{
    protected override IMetricsHistoryStore CreateStore(string dir) =>
        new SqliteMetricsHistoryStore(Path.Combine(dir, "metrics.db"));
}

public sealed class BinaryScalarDecimatedRawSpecTests : ScalarDecimatedRawSpec
{
    protected override IMetricsHistoryStore CreateStore(string dir) =>
        new BinaryMetricsHistoryStore(dir);
}
