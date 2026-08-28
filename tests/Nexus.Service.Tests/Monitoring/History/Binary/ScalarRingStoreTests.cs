using System;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

/// <summary>
/// ScalarRingStore-level coverage for behavior the shared IMetricsHistoryStore
/// specs (which exercise it only indirectly, through BinaryMetricsHistoryStore
/// at the real 7-day capacity) cannot easily reach: a small, test-chosen
/// capacity so wraparound and whole-ring-scan queries are cheap to construct,
/// and the exact null/absent and clamping semantics ScalarRingStore itself
/// owns.
/// </summary>
public class ScalarRingStoreTests : IDisposable
{
    private readonly string _dir;

    public ScalarRingStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-scalarring-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ScalarRingStore CreateStore(long capacity = 100, long initialPruneFloorSec = long.MinValue) =>
        new(Path.Combine(_dir, "scalars.ring"), capacity, initialPruneFloorSec);

    private static MetricSample Scalars(
        long ts, double? cpu = 50, double? mem = 60, double? netIn = 1000, double? netOut = 500, double? cpuTemp = 55, int? fps = null) =>
        new(ts, cpu, mem, netIn, netOut, cpuTemp, Array.Empty<GpuReading>(), Array.Empty<FanReading>(), Fps: fps);

    [Fact]
    public void Append_ThenQuery_RoundTripsEveryField()
    {
        using var store = CreateStore();
        store.Append(new[] { Scalars(10, cpu: 42.3, mem: 61.7, netIn: 12345, netOut: 6789, cpuTemp: 55.4) });

        var row = Assert.Single(store.Query(0, 100));
        Assert.Equal(10, row.TsSec);
        Assert.Equal(42.3, row.CpuPercent);
        Assert.Equal(61.7, row.MemoryPercent);
        Assert.Equal(12345, row.NetInBytesPerSec);
        Assert.Equal(6789, row.NetOutBytesPerSec);
        Assert.Equal(55.4, row.CpuTempC);
    }

    [Fact]
    public void Append_AnFpsReading_RoundTrips()
    {
        using var store = CreateStore();
        store.Append(new[] { Scalars(10, fps: 144) });

        var row = Assert.Single(store.Query(0, 100));
        Assert.Equal(144, row.Fps);
    }

    [Fact]
    public void Append_FpsZero_RoundTripsAsNull_UnlikeEveryOtherScalarField()
    {
        // Decision: frames == 0 (loading, paused, minimized) reads as an
        // absent second on the fps series, not as a genuine zero.
        using var store = CreateStore();
        store.Append(new[] { Scalars(10, fps: 0) });

        var row = Assert.Single(store.Query(0, 100));
        Assert.Null(row.Fps);
    }

    [Fact]
    public void Append_NoFpsReading_RoundTripsAsNull()
    {
        using var store = CreateStore();
        store.Append(new[] { Scalars(10) });

        var row = Assert.Single(store.Query(0, 100));
        Assert.Null(row.Fps);
    }

    [Fact]
    public void BlankFps_ClearsOnlyFps_LeavingEveryOtherFieldOnTheSameSlotIntact()
    {
        using var store = CreateStore();
        store.Append(new[] { Scalars(10, cpu: 42, fps: 144) });

        var cleared = store.BlankFps();

        var row = Assert.Single(store.Query(0, 100));
        Assert.Equal(1, cleared);
        Assert.Null(row.Fps);
        Assert.Equal(42, row.CpuPercent);
    }

    [Fact]
    public void BlankFps_SlotWithNoFpsReading_IsNotCounted()
    {
        using var store = CreateStore();
        store.Append(new[] { Scalars(10, fps: null) });

        Assert.Equal(0, store.BlankFps());
    }

    [Fact]
    public void Append_AZeroReading_RoundTripsAsZero_NotAsNull()
    {
        // Zero is a legitimate reading (idle CPU, no network traffic) and
        // must not collide with the null-sentinel encoding.
        using var store = CreateStore();
        store.Append(new[] { Scalars(10, cpu: 0, mem: 0, netIn: 0, netOut: 0, cpuTemp: 0) });

        var row = Assert.Single(store.Query(0, 100));
        Assert.Equal(0, row.CpuPercent);
        Assert.Equal(0, row.MemoryPercent);
        Assert.Equal(0, row.NetInBytesPerSec);
        Assert.Equal(0, row.NetOutBytesPerSec);
        Assert.Equal(0, row.CpuTempC);
    }

    [Fact]
    public void Query_ANullField_IsDistinctFromAnAbsentTimestamp()
    {
        using var store = CreateStore();
        store.Append(new[] { Scalars(10, cpu: null, mem: 60) });

        var rows = store.Query(0, 20);

        var row = Assert.Single(rows); // only ts=10 has a row at all
        Assert.Null(row.CpuPercent);   // present row, failed field: null
        Assert.Equal(60, row.MemoryPercent);
        Assert.DoesNotContain(rows, r => r.TsSec == 11); // never appended: no row, not a zeroed one
    }

    [Fact]
    public void Append_SameTimestampTwice_ReplacesRatherThanDuplicating()
    {
        using var store = CreateStore();
        store.Append(new[] { Scalars(10, cpu: 10) });
        store.Append(new[] { Scalars(10, cpu: 90) });

        var row = Assert.Single(store.Query(0, 100));
        Assert.Equal(90, row.CpuPercent);
    }

    [Fact]
    public void Query_WithARangeWiderThanCapacity_StillReturnsOnlyRealRows()
    {
        using var store = CreateStore(capacity: 5);
        store.Append(new[] { Scalars(0, cpu: 1), Scalars(1, cpu: 2), Scalars(2, cpu: 3) });

        var rows = store.Query(0, 100_000); // far wider than the ring's capacity

        Assert.Equal(new long[] { 0, 1, 2 }, rows.Select(r => r.TsSec).ToArray());
        Assert.Equal(new double?[] { 1, 2, 3 }, rows.Select(r => r.CpuPercent).ToArray());
    }

    [Fact]
    public void Query_OnAnEmptyStore_WithAPathologicallyWideRange_ReturnsEmpty()
    {
        using var store = CreateStore(capacity: 5);

        Assert.Empty(store.Query(0, long.MaxValue));
    }

    [Fact]
    public void RaisePruneFloor_NeverLowersAnAlreadyHigherFloor()
    {
        using var store = CreateStore();

        Assert.True(store.RaisePruneFloor(1000));
        Assert.False(store.RaisePruneFloor(500)); // lower: rejected
        Assert.Equal(1000, store.PruneFloorSec);
    }

    [Fact]
    public void RaisePruneFloor_HidesOlderRows_ButLeavesNewerRowsQueryable()
    {
        using var store = CreateStore();
        store.Append(new[] { Scalars(1000, cpu: 10), Scalars(5000, cpu: 20) });

        store.RaisePruneFloor(3000);

        var row = Assert.Single(store.Query(0, 10_000));
        Assert.Equal(5000, row.TsSec);
    }

    [Fact]
    public void Append_AnExtremeOutOfRangeReading_ClampsRatherThanWrapping()
    {
        // 99_999_999 * 10 vastly exceeds short.MaxValue; an unchecked cast
        // would silently wrap to a small or negative-looking value instead
        // of the (still wrong, but at least directionally sane) clamped max.
        using var store = CreateStore();
        store.Append(new[] { Scalars(10, cpu: 99_999_999) });

        var row = Assert.Single(store.Query(0, 100));
        Assert.Equal(short.MaxValue / 10.0, row.CpuPercent); // clamped to the largest representable x10 value
    }

    [Fact]
    public void Append_ANonFiniteReading_IsTreatedAsNull()
    {
        using var store = CreateStore();
        store.Append(new[] { Scalars(10, cpu: double.NaN, mem: double.PositiveInfinity, cpuTemp: double.NegativeInfinity) });

        var row = Assert.Single(store.Query(0, 100));
        Assert.Null(row.CpuPercent);
        Assert.Null(row.MemoryPercent);
        Assert.Null(row.CpuTempC);
    }

    [Fact]
    public void QueryScalarsDecimatedRaw_AveragesAndMaxesWithinASlot()
    {
        using var store = CreateStore();
        store.Append(new[] { Scalars(0, cpu: 10), Scalars(1, cpu: 30) });

        var slot = Assert.Single(store.QueryScalarsDecimatedRaw(0, 1, stepSeconds: 10));

        Assert.Equal(0, slot.Slot);
        Assert.Equal(20, slot.CpuAvg);
        Assert.Equal(30, slot.CpuMax);
    }

    [Fact]
    public void QueryScalarsDecimatedRaw_NoDataAtAll_ProducesNoSlot_UnlikeANullField()
    {
        using var store = CreateStore();
        store.Append(new[] { Scalars(0, cpu: null) });

        var withNullField = store.QueryScalarsDecimatedRaw(0, 9, stepSeconds: 10);
        var withNoDataAtAll = store.QueryScalarsDecimatedRaw(1000, 1009, stepSeconds: 10);

        var slot = Assert.Single(withNullField);
        Assert.Null(slot.CpuAvg); // a row existed at ts=0; its cpu field failed
        Assert.Empty(withNoDataAtAll); // no row existed anywhere in this window
    }
}
