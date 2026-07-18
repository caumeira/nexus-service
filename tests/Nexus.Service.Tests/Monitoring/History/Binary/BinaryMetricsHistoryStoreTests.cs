using System;
using System.IO;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

/// <summary>
/// Facade-level tests for BinaryMetricsHistoryStore: proves ScalarRingStore
/// and SuperBlock are wired together correctly (construction creates both
/// files under the given directory, a persisted prune floor survives a
/// reopen) and that the still-unimplemented per-entity/temperature surface
/// fails loudly rather than silently returning wrong data. The exhaustive
/// scalar semantics (null/absent, clamping, wraparound) are covered at the
/// ScalarRingStore/RingFile level - this file only checks the facade wiring.
/// </summary>
public class BinaryMetricsHistoryStoreTests : IDisposable
{
    private readonly string _dir;

    public BinaryMetricsHistoryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-binaryhistory-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static MetricSample Scalars(long ts, double? cpu = 50) =>
        new(ts, cpu, 60, 1000, 500, 55, Array.Empty<GpuReading>(), Array.Empty<FanReading>());

    [Fact]
    public void Constructor_CreatesBothTheSuperblockAndTheScalarRingFiles()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);

        Assert.True(File.Exists(Path.Combine(_dir, "super")));
        Assert.True(File.Exists(Path.Combine(_dir, "scalars.ring")));
    }

    [Fact]
    public void Query_ANullField_IsDistinctFromAnAbsentTimestamp()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);
        store.Append(new[] { Scalars(10, cpu: null) }, null);

        var rows = store.Query(0, 20);

        var row = Assert.Single(rows);
        Assert.Equal(10, row.TsSec);
        Assert.Null(row.CpuPercent);
        Assert.DoesNotContain(rows, r => r.TsSec == 11);
    }

    [Fact]
    public void QueryScalarsDecimated_NoDataAtAll_ProducesNoSlot_UnlikeANullField()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);
        store.Append(new[] { Scalars(0, cpu: null) }, null);

        var withNullField = store.QueryScalarsDecimated(0, 9, stepSeconds: 10);
        var withNoDataAtAll = store.QueryScalarsDecimated(10_000, 10_009, stepSeconds: 10);

        var slot = Assert.Single(withNullField);
        Assert.Null(slot.CpuAvg);
        Assert.Empty(withNoDataAtAll);
    }

    [Fact]
    public void Append_WithPruneCutoff_ThenReopen_TheFloorSurvivesTheRestart()
    {
        using (var store = new BinaryMetricsHistoryStore(_dir))
        {
            store.Append(new[] { Scalars(1000), Scalars(5000) }, pruneCutoffSec: 3000);
        }

        using var reopened = new BinaryMetricsHistoryStore(_dir);
        var row = Assert.Single(reopened.Query(0, 10_000));
        Assert.Equal(5000, row.TsSec);
    }

    [Fact]
    public void QueryScalarsDecimated_WithARollupEligibleStep_ThrowsNotImplemented_NotWrongData()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);
        store.Append(new[] { Scalars(0) }, null);

        Assert.Throws<NotImplementedException>(() => store.QueryScalarsDecimated(0, 119, stepSeconds: 60));
    }

    [Fact]
    public void QueryGpuDecimated_ThrowsNotImplemented()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);
        Assert.Throws<NotImplementedException>(() => store.QueryGpuDecimated(0, 100, stepSeconds: 10));
    }

    [Fact]
    public void QueryFanDecimated_ThrowsNotImplemented()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);
        Assert.Throws<NotImplementedException>(() => store.QueryFanDecimated(0, 100, stepSeconds: 10));
    }

    [Fact]
    public void QueryComponentTempDecimated_ThrowsNotImplemented()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);
        Assert.Throws<NotImplementedException>(() => store.QueryComponentTempDecimated(0, 100, stepSeconds: 10));
    }

    [Fact]
    public void QueryTemperatureBuckets_ThrowsNotImplemented()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);
        Assert.Throws<NotImplementedException>(() => store.QueryTemperatureBuckets(0, 100_000));
    }

    [Fact]
    public void Append_IgnoresGpuFanAndComponentTempReadings_WithoutThrowing()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);
        var sample = new MetricSample(10, 50, 60, 1000, 500, 55,
            new[] { new GpuReading("gpu-0", "GPU", "", 10, 40) },
            new[] { new FanReading("fan-0", "Fan", 1000, 50) })
        {
            ComponentTemps = new[] { new ComponentTempReading("ram:0", "ram", "DIMM", 40) },
        };

        store.Append(new[] { sample }, null);

        var row = Assert.Single(store.Query(0, 100));
        Assert.Equal(50, row.CpuPercent);
        Assert.Empty(row.Gpus);
        Assert.Empty(row.Fans);
        Assert.Empty(row.ComponentTemps);
    }
}
