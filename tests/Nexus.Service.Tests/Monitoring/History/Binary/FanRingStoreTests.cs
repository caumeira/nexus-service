using System;
using System.IO;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

/// <summary>
/// FanRingStore-level coverage mirroring GpuRingStoreTests' shape, focused on
/// what actually differs for fans: rpm/duty are plain whole ints
/// (FixedPointCodec.ScaleInt16), not gpu's x10 fixed point. See
/// GpuRingStoreTests for the entity-ring wraparound case both kinds share.
/// </summary>
public class FanRingStoreTests : IDisposable
{
    private readonly string _dir;

    public FanRingStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-fanring-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private FanRingStore CreateStore(long secondCapacity = 1000, long minuteCapacity = 100, int entityCapacity = 32) =>
        new(_dir, secondCapacity, minuteCapacity, entityCapacity, initialPruneFloorSec: long.MinValue);

    private static MetricSample FanSample(long ts, params FanReading[] fans) =>
        new(ts, null, null, null, null, null, Array.Empty<GpuReading>(), fans);

    [Fact]
    public void Append_ThenQuery_RoundTripsRpmAndDuty_NotX10Scaled()
    {
        using var store = CreateStore();
        store.Append(new[] { FanSample(10, new FanReading("fan-0", "Fan 1", 1234, 56)) });

        var reading = Assert.Single(Assert.Single(store.Query(0, 100)).Value);
        Assert.Equal("fan-0", reading.FanId);
        Assert.Equal(1234, reading.Rpm);
        Assert.Equal(56, reading.Duty);
    }

    [Fact]
    public void Append_ANullRpm_RoundTripsAsNull_NotZero()
    {
        // FanChannel.RpmUnavailable channels report duty with no rpm.
        using var store = CreateStore();
        store.Append(new[] { FanSample(10, new FanReading("fan-0", "Fan 1", null, 56)) });

        var reading = Assert.Single(Assert.Single(store.Query(0, 100)).Value);
        Assert.Null(reading.Rpm);
        Assert.Equal(56, reading.Duty);
    }

    [Fact]
    public void Append_ANewFan_ExtendsThePool_WithItsOwnRingFiles()
    {
        using var store = CreateStore();
        store.Append(new[] { FanSample(0, new FanReading("fan-0", "A", 1000, 40)) });
        store.Append(new[] { FanSample(0, new FanReading("fan-1", "B", 2000, 60)) });

        Assert.True(File.Exists(Path.Combine(_dir, "0.ring")));
        Assert.True(File.Exists(Path.Combine(_dir, "1.ring")));

        var readings = store.Query(0, 0)[0];
        Assert.Contains(readings, r => r.FanId == "fan-0" && r.Rpm == 1000);
        Assert.Contains(readings, r => r.FanId == "fan-1" && r.Rpm == 2000);
    }

    [Fact]
    public void QueryRollupDecimated_AccumulatesAcrossFlushes()
    {
        using var store = CreateStore();
        store.Append(new[] { FanSample(0, new FanReading("fan-0", "Fan 1", 1000, 40)) });
        store.Append(new[] { FanSample(30, new FanReading("fan-0", "Fan 1", 1200, 60)) });

        var slot = Assert.Single(store.QueryRollupDecimated(0, 59, stepSeconds: 60));

        Assert.Equal(1100, slot.RpmAvg);
        Assert.Equal(1200, slot.RpmMax);
        Assert.Equal(50, slot.DutyAvg);
    }
}
