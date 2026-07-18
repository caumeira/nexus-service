using System;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

/// <summary>
/// ScalarMinuteRollupRing-level coverage for behavior the shared
/// RollupHistorySpec (which exercises it only indirectly, through
/// BinaryMetricsHistoryStore at the real 7-day capacity) cannot easily
/// reach: a small, test-chosen minute capacity so wraparound is cheap to
/// construct, RebuildMinute's overwrite-not-accumulate contract in
/// isolation, and MinuteTier.ToPruneFloorIndex's ceiling conversion for a
/// non-minute-aligned cutoff.
/// </summary>
public class ScalarMinuteRollupRingTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ScalarMinuteRollupRingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-scalarminuterollup-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "scalars.min");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ScalarMinuteRollupRing CreateRing(long minuteCapacity = 100, long initialPruneFloorSec = long.MinValue) =>
        new(_path, minuteCapacity, initialPruneFloorSec);

    private static ScalarMinuteAgg CpuOnly(double sum, int cnt, double max) =>
        new(new FieldAgg(sum, cnt, max), FieldAgg.Empty, FieldAgg.Empty, FieldAgg.Empty, FieldAgg.Empty);

    [Fact]
    public void RebuildMinute_ThenQueryDecimated_RoundTrips()
    {
        using var ring = CreateRing();
        ring.RebuildMinute(0, CpuOnly(sum: 20, cnt: 2, max: 30));

        var slot = Assert.Single(ring.QueryDecimated(0, 59, stepSeconds: 60));

        Assert.Equal(10, slot.CpuAvg);
        Assert.Equal(30, slot.CpuMax);
        Assert.Null(slot.MemAvg); // never touched: no field-null sentinel needed, Cnt==0 already means this
    }

    [Fact]
    public void RebuildMinute_RewritingTheSameMinute_OverwritesRatherThanAccumulating()
    {
        using var ring = CreateRing();
        ring.RebuildMinute(0, CpuOnly(sum: 10, cnt: 1, max: 10));
        ring.RebuildMinute(0, CpuOnly(sum: 60, cnt: 2, max: 50)); // a later rebuild replaces the slot outright

        var slot = Assert.Single(ring.QueryDecimated(0, 59, stepSeconds: 60));

        Assert.Equal(30, slot.CpuAvg); // 60/2, not folded with the earlier write
        Assert.Equal(50, slot.CpuMax);
    }

    [Fact]
    public void QueryDecimated_FoldsSeveralMinutesIntoAWiderStep()
    {
        using var ring = CreateRing();
        ring.RebuildMinute(0, CpuOnly(sum: 10, cnt: 1, max: 10));
        ring.RebuildMinute(60, CpuOnly(sum: 50, cnt: 1, max: 50));

        var slot = Assert.Single(ring.QueryDecimated(0, 119, stepSeconds: 120));

        Assert.Equal(30, slot.CpuAvg); // (10+50)/2 across both minutes
        Assert.Equal(50, slot.CpuMax);
    }

    [Fact]
    public void Ring_WrapsPastMinuteCapacity_OldMinuteReadsAsAbsent()
    {
        using var ring = CreateRing(minuteCapacity: 5);
        ring.RebuildMinute(0, CpuOnly(sum: 10, cnt: 1, max: 10));
        ring.RebuildMinute(300, CpuOnly(sum: 20, cnt: 1, max: 20)); // minute index 5 == minute index 0, mod 5

        var slots = ring.QueryDecimated(0, 359, stepSeconds: 60);

        Assert.DoesNotContain(slots, s => s.Slot == 0);
        var survivor = Assert.Single(slots);
        Assert.Equal(300, survivor.Slot);
        Assert.Equal(20, survivor.CpuAvg);
    }

    [Fact]
    public void RaisePruneFloor_DropsAMinuteWhoseFloorPrecedesANonMinuteAlignedCutoff()
    {
        // cutoff=59 is not minute-aligned; minute 0's floor (0) is before
        // it, minute 60's floor (60) is not - only the former must drop.
        using var ring = CreateRing();
        ring.RebuildMinute(0, CpuOnly(sum: 10, cnt: 1, max: 10));
        ring.RebuildMinute(60, CpuOnly(sum: 20, cnt: 1, max: 20));

        ring.RaisePruneFloor(59);

        var slots = ring.QueryDecimated(0, 119, stepSeconds: 60);
        Assert.DoesNotContain(slots, s => s.Slot == 0);
        Assert.Contains(slots, s => s.Slot == 60);
    }

    [Fact]
    public void RaisePruneFloor_NeverLowersAnAlreadyHigherFloor()
    {
        using var ring = CreateRing();

        Assert.True(ring.RaisePruneFloor(120));
        Assert.False(ring.RaisePruneFloor(60)); // lower: rejected
    }
}
