using System;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// The metric_minutes/gpu_minutes/fan_minutes rollup tier: incremental
/// upsert at flush time, and the QueryXDecimated dispatch that serves
/// step&gt;=60 (rollup-eligible, per MetricsHistory.StepLadderSeconds) from
/// it instead of GROUP BY over the raw per-second tables.
/// </summary>
public class RollupHistoryStoreTests : IDisposable
{
    private readonly string _dir;
    private SqliteMetricsHistoryStore _store;

    public RollupHistoryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-rolluphistory-" + Guid.NewGuid().ToString("N")[..8]);
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
    public void QueryScalarsDecimated_AtStep60_MatchesTheRawPathForTheSameWindow()
    {
        var samples = Enumerable.Range(0, 120).Select(i => Scalars(i, cpu: i % 50)).ToArray();
        _store.Append(samples, null);

        var rollup = _store.QueryScalarsDecimated(0, 119, stepSeconds: 60);
        var raw = _store.QueryScalarsDecimated(0, 119, stepSeconds: 10);
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
        _store.Append(new[] { Scalars(0, cpu: 10), Scalars(1, cpu: 20) }, null);
        _store.Append(new[] { Scalars(2, cpu: 90), Scalars(3, cpu: 30) }, null);

        var slot = Assert.Single(_store.QueryScalarsDecimated(0, 59, stepSeconds: 60));

        Assert.Equal((10 + 20 + 90 + 30) / 4.0, slot.CpuAvg!.Value, precision: 3);
        Assert.Equal(90, slot.CpuMax);
    }

    [Fact]
    public void Rollup_LeavesAFieldNull_WhenNoFlushEverReportedIt()
    {
        _store.Append(new[] { Scalars(0, cpu: null) }, null);

        var slot = Assert.Single(_store.QueryScalarsDecimated(0, 59, stepSeconds: 60));

        Assert.Null(slot.CpuAvg);
        Assert.Null(slot.CpuMax);
    }

    [Fact]
    public void Rollup_KeepsAPartialFieldCorrect_WhenOnlyOneOfTwoFlushesHadData()
    {
        _store.Append(new[] { Scalars(0, cpu: null) }, null);
        _store.Append(new[] { Scalars(1, cpu: 40) }, null);

        var slot = Assert.Single(_store.QueryScalarsDecimated(0, 59, stepSeconds: 60));

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
        _store.Append(new[] { Scalars(60, cpu: 10), Scalars(119, cpu: 30) }, null);

        var slot = Assert.Single(_store.QueryScalarsDecimated(100, 200, stepSeconds: 60));

        Assert.Equal(60, slot.Slot);
        Assert.Equal(20, slot.CpuAvg);
    }

    [Fact]
    public void Rollup_ExcludesAMinute_EntirelyOutsideTheWindow()
    {
        _store.Append(new[] { Scalars(0, cpu: 10) }, null); // minute [0,59]

        var result = _store.QueryScalarsDecimated(100, 200, stepSeconds: 60);

        Assert.Empty(result);
    }

    [Fact]
    public void Rollup_SplitsAcrossMinuteBoundaries()
    {
        _store.Append(new[] { Scalars(59, cpu: 10), Scalars(60, cpu: 90) }, null);

        var slots = _store.QueryScalarsDecimated(0, 119, stepSeconds: 60).OrderBy(s => s.Slot).ToList();

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
        _store.Append(new[] { s1 }, null);
        _store.Append(new[] { s2 }, null);

        var slot = Assert.Single(_store.QueryGpuDecimated(0, 59, stepSeconds: 60));

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
        _store.Append(new[] { s }, null);

        var slot = Assert.Single(_store.QueryGpuDecimated(100, 200, stepSeconds: 60));

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
        _store.Append(new[] { s1 }, null);
        _store.Append(new[] { s2 }, null);

        var slot = Assert.Single(_store.QueryFanDecimated(0, 59, stepSeconds: 60));

        Assert.Equal(1100, slot.RpmAvg);
        Assert.Equal(1200, slot.RpmMax);
        Assert.Equal(50, slot.DutyAvg);
    }

    [Fact]
    public void Rollup_IsPruned_WithTheSameHourlyCutoffAsTheRawTables()
    {
        _store.Append(new[] { Scalars(0, cpu: 10) }, null);

        _store.Append(Array.Empty<MetricSample>(), pruneCutoffSec: 3000);

        Assert.Empty(_store.QueryScalarsDecimated(0, 59, stepSeconds: 60));
    }

    [Fact]
    public void Rollup_KeepsRowsAtOrAfterTheCutoff()
    {
        _store.Append(new[] { Scalars(5000, cpu: 10) }, null);

        _store.Append(Array.Empty<MetricSample>(), pruneCutoffSec: 3000);

        Assert.Single(_store.QueryScalarsDecimated(4980, 5010, stepSeconds: 60));
    }

    [Fact]
    public void Rollup_SurvivesReopen()
    {
        _store.Append(new[] { Scalars(0, cpu: 10), Scalars(1, cpu: 30) }, null);
        _store.Dispose();

        _store = new SqliteMetricsHistoryStore(Path.Combine(_dir, "metrics.db"));

        var slot = Assert.Single(_store.QueryScalarsDecimated(0, 59, stepSeconds: 60));
        Assert.Equal(20, slot.CpuAvg);
    }
}
