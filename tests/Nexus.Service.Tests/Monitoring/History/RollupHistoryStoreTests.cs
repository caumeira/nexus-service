using System;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// The minute-rollup tier - ScalarMinuteRollupRing/GpuRingStore/FanRingStore's
/// minute rings: incremental upsert/rebuild at flush time, and the
/// QueryXDecimated dispatch that serves step&gt;=60 (rollup-eligible, per
/// MetricsHistory.StepLadderSeconds) from it instead of aggregating the raw
/// per-second data directly. Runs against BinaryMetricsHistoryStore
/// (BinaryRollupHistorySpecTests).
/// </summary>
public abstract class RollupHistorySpec : IDisposable
{
    private readonly string _dir;
    protected IMetricsHistoryStore Store;

    protected RollupHistorySpec()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-rolluphistory-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        Store = CreateStore(_dir);
    }

    protected abstract IMetricsHistoryStore CreateStore(string dir);

    public virtual void Dispose()
    {
        Store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    protected static MetricSample Scalars(
        long ts, double? cpu, double? mem = 10, double? netIn = 100, double? netOut = 50, double? cpuTemp = 40,
        double? diskRead = null, double? diskWrite = null) =>
        new(ts, cpu, mem, netIn, netOut, cpuTemp, Array.Empty<GpuReading>(), Array.Empty<FanReading>(),
            DiskReadBytesPerSec: diskRead, DiskWriteBytesPerSec: diskWrite);

    [Fact]
    public void DiskRollup_AccumulatesAcrossTwoFlushesLandingInTheSameMinute()
    {
        Store.Append(new[] { Scalars(0, cpu: 10, diskRead: 1000, diskWrite: 200), Scalars(1, cpu: 20, diskRead: 3000, diskWrite: 600) }, null);
        Store.Append(new[] { Scalars(2, cpu: 90, diskRead: 2000, diskWrite: 400), Scalars(3, cpu: 30, diskRead: 2000, diskWrite: 400) }, null);

        var slot = Assert.Single(Store.QueryScalarsDecimated(0, 59, stepSeconds: 60));

        Assert.Equal((1000 + 3000 + 2000 + 2000) / 4.0, slot.DiskReadAvg!.Value, precision: 3);
        Assert.Equal(3000, slot.DiskReadMax);
        Assert.Equal((200 + 600 + 400 + 400) / 4.0, slot.DiskWriteAvg!.Value, precision: 3);
        Assert.Equal(600, slot.DiskWriteMax);
    }

    [Fact]
    public void QueryScalarsDecimated_AtStep60_MatchesTheRawPathForTheSameWindow()
    {
        var samples = Enumerable.Range(0, 120).Select(i => Scalars(i, cpu: i % 50)).ToArray();
        Store.Append(samples, null);

        var rollup = Store.QueryScalarsDecimated(0, 119, stepSeconds: 60);
        var raw = Store.QueryScalarsDecimated(0, 119, stepSeconds: 10);
        // Re-slot the fine-grained raw result to the same 60s boundaries a
        // human would expect the rollup to reproduce exactly.
        var rawBySlot = raw.GroupBy(s => s.Slot / 60 * 60);

        Assert.Equal(2, rollup.Count);
        foreach (var slot in rollup)
        {
            var covering = rawBySlot.Single(g => g.Key == slot.Slot);
            var expectedAvg = covering.Average(s => s.CpuAvg!.Value);
            Assert.Equal(expectedAvg, slot.CpuAvg!.Value, precision: 3);
        }
    }

    [Fact]
    public void Rollup_AccumulatesAcrossTwoFlushesLandingInTheSameMinute()
    {
        // Two separate Append calls (two flushes), both inside second 0-59,
        // must combine into one minute's avg/max rather than the second
        // flush overwriting the first.
        Store.Append(new[] { Scalars(0, cpu: 10), Scalars(1, cpu: 20) }, null);
        Store.Append(new[] { Scalars(2, cpu: 90), Scalars(3, cpu: 30) }, null);

        var slot = Assert.Single(Store.QueryScalarsDecimated(0, 59, stepSeconds: 60));

        Assert.Equal((10 + 20 + 90 + 30) / 4.0, slot.CpuAvg!.Value, precision: 3);
        Assert.Equal(90, slot.CpuMax);
    }

    [Fact]
    public void Rollup_LeavesAFieldNull_WhenNoFlushEverReportedIt()
    {
        Store.Append(new[] { Scalars(0, cpu: null) }, null);

        var slot = Assert.Single(Store.QueryScalarsDecimated(0, 59, stepSeconds: 60));

        Assert.Null(slot.CpuAvg);
        Assert.Null(slot.CpuMax);
    }

    [Fact]
    public void Rollup_KeepsAPartialFieldCorrect_WhenOnlyOneOfTwoFlushesHadData()
    {
        Store.Append(new[] { Scalars(0, cpu: null) }, null);
        Store.Append(new[] { Scalars(1, cpu: 40) }, null);

        var slot = Assert.Single(Store.QueryScalarsDecimated(0, 59, stepSeconds: 60));

        Assert.Equal(40, slot.CpuAvg);
        Assert.Equal(40, slot.CpuMax);
    }

    [Fact]
    public void Rollup_IncludesAMinute_WhenOnlyPartOfItIsInsideTheWindow()
    {
        // ts_min for these samples floors to 60 (minute [60,119]); a query
        // for [100,200] starts mid-minute. A tight ts_min BETWEEN from AND
        // to would drop the whole minute since ts_min=60 < from=100, even
        // though seconds 100-119 of it are inside the window.
        Store.Append(new[] { Scalars(60, cpu: 10), Scalars(119, cpu: 30) }, null);

        var slot = Assert.Single(Store.QueryScalarsDecimated(100, 200, stepSeconds: 60));

        Assert.Equal(60, slot.Slot);
        Assert.Equal(20, slot.CpuAvg);
    }

    [Fact]
    public void Rollup_ExcludesAMinute_EntirelyOutsideTheWindow()
    {
        Store.Append(new[] { Scalars(0, cpu: 10) }, null); // minute [0,59]

        var result = Store.QueryScalarsDecimated(100, 200, stepSeconds: 60);

        Assert.Empty(result);
    }

    [Fact]
    public void Rollup_SplitsAcrossMinuteBoundaries()
    {
        Store.Append(new[] { Scalars(59, cpu: 10), Scalars(60, cpu: 90) }, null);

        var slots = Store.QueryScalarsDecimated(0, 119, stepSeconds: 60).OrderBy(s => s.Slot).ToList();

        Assert.Equal(2, slots.Count);
        Assert.Equal(0, slots[0].Slot);
        Assert.Equal(10, slots[0].CpuAvg);
        Assert.Equal(60, slots[1].Slot);
        Assert.Equal(90, slots[1].CpuAvg);
    }

    [Fact]
    public void GpuRollup_AccumulatesAcrossFlushes_KeyedByGpuId()
    {
        var s1 = new MetricSample(0, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 10, 40) }, Array.Empty<FanReading>());
        var s2 = new MetricSample(30, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 50, 44) }, Array.Empty<FanReading>());
        Store.Append(new[] { s1 }, null);
        Store.Append(new[] { s2 }, null);

        var slot = Assert.Single(Store.QueryGpuDecimated(0, 59, stepSeconds: 60));

        Assert.Equal("gpu-0", slot.GpuId);
        Assert.Equal(30, slot.LoadAvg);
        Assert.Equal(50, slot.LoadMax);
        Assert.Equal(42, slot.TempAvg);
    }

    [Fact]
    public void GpuRollup_IncludesAMinute_WhenOnlyPartOfItIsInsideTheWindow()
    {
        var s = new MetricSample(60, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 40, 60) }, Array.Empty<FanReading>());
        Store.Append(new[] { s }, null);

        var slot = Assert.Single(Store.QueryGpuDecimated(100, 200, stepSeconds: 60));

        Assert.Equal(60, slot.Slot);
        Assert.Equal(40, slot.LoadAvg);
    }

    [Fact]
    public void FanRollup_AccumulatesAcrossFlushes_NotX10Scaled()
    {
        var s1 = new MetricSample(0, null, null, null, null, null,
            Array.Empty<GpuReading>(), new[] { new FanReading("fan-0", "Fan 1", 1000, 40) });
        var s2 = new MetricSample(30, null, null, null, null, null,
            Array.Empty<GpuReading>(), new[] { new FanReading("fan-0", "Fan 1", 1200, 60) });
        Store.Append(new[] { s1 }, null);
        Store.Append(new[] { s2 }, null);

        var slot = Assert.Single(Store.QueryFanDecimated(0, 59, stepSeconds: 60));

        Assert.Equal(1100, slot.RpmAvg);
        Assert.Equal(1200, slot.RpmMax);
        Assert.Equal(50, slot.DutyAvg);
    }

    [Fact]
    public void Rollup_IsPruned_WithTheSameHourlyCutoffAsTheRawTables()
    {
        Store.Append(new[] { Scalars(0, cpu: 10) }, null);

        Store.Append(Array.Empty<MetricSample>(), pruneCutoffSec: 3000);

        Assert.Empty(Store.QueryScalarsDecimated(0, 59, stepSeconds: 60));
    }

    [Fact]
    public void Rollup_KeepsRowsAtOrAfterTheCutoff()
    {
        Store.Append(new[] { Scalars(5000, cpu: 10) }, null);

        Store.Append(Array.Empty<MetricSample>(), pruneCutoffSec: 3000);

        Assert.Single(Store.QueryScalarsDecimated(4980, 5010, stepSeconds: 60));
    }

    [Fact]
    public void Rollup_SurvivesReopen()
    {
        Store.Append(new[] { Scalars(0, cpu: 10), Scalars(1, cpu: 30) }, null);
        Store.Dispose();

        Store = CreateStore(_dir);

        var slot = Assert.Single(Store.QueryScalarsDecimated(0, 59, stepSeconds: 60));
        Assert.Equal(20, slot.CpuAvg);
    }

    [Fact]
    public void Rollup_DoesNotDoubleCount_WhenATsIsReplayedInASecondFlush()
    {
        // A backward clock step can cause the sampler to reobserve and
        // reflush a ts already committed in an earlier flush. The raw
        // table dedupes it via INSERT OR REPLACE; the rollup must match -
        // an additive accumulation would count ts=1 twice here.
        Store.Append(new[] { Scalars(0, cpu: 10), Scalars(1, cpu: 30) }, null);
        Store.Append(new[] { Scalars(1, cpu: 30) }, null);

        var slot = Assert.Single(Store.QueryScalarsDecimated(0, 59, stepSeconds: 60));

        Assert.Equal((10 + 30) / 2.0, slot.CpuAvg!.Value, precision: 3);
    }

    [Fact]
    public void Rollup_MatchesARawRecompute_WhenAReplayedTsCarriesADifferentReading()
    {
        Store.Append(new[] { Scalars(0, cpu: 10), Scalars(1, cpu: 30) }, null);
        Store.Append(new[] { Scalars(1, cpu: 90) }, null);

        var slot = Assert.Single(Store.QueryScalarsDecimated(0, 59, stepSeconds: 60));
        var rawRecompute = Store.QueryScalarsDecimated(0, 59, stepSeconds: 2);

        Assert.Equal((10 + 90) / 2.0, slot.CpuAvg!.Value, precision: 3);
        Assert.Equal(90, slot.CpuMax);
        Assert.Equal(rawRecompute.Where(s => s.CpuAvg is not null).Average(s => s.CpuAvg!.Value), slot.CpuAvg!.Value, precision: 3);
    }

    [Fact]
    public void GpuRollup_DoesNotDoubleCount_WhenATsIsReplayedInASecondFlush()
    {
        var s1 = new MetricSample(0, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 10, 40) }, Array.Empty<FanReading>());
        var s2 = new MetricSample(30, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 50, 44) }, Array.Empty<FanReading>());
        Store.Append(new[] { s1 }, null);
        Store.Append(new[] { s2 }, null);
        Store.Append(new[] { s2 }, null); // s2's ts replayed, unchanged

        var slot = Assert.Single(Store.QueryGpuDecimated(0, 59, stepSeconds: 60));

        Assert.Equal(30, slot.LoadAvg);
        Assert.Equal(50, slot.LoadMax);
    }
}

public sealed class BinaryRollupHistorySpecTests : RollupHistorySpec
{
    protected override IMetricsHistoryStore CreateStore(string dir) =>
        new BinaryMetricsHistoryStore(dir);
}
