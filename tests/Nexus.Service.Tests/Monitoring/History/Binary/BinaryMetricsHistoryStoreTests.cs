using System;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

/// <summary>
/// Facade-level tests for BinaryMetricsHistoryStore: proves ScalarRingStore
/// and SuperBlock are wired together correctly (construction creates both
/// files under the given directory, a persisted prune floor survives a
/// reopen). The exhaustive scalar/gpu/fan/temp-component semantics (null/
/// absent, clamping, wraparound, rollup rebuild) are covered at the
/// ScalarRingStore/GpuRingStore/FanRingStore/TempComponentRingStore/
/// TempBucketStore/RingFile level and by the shared specs parameterized over
/// both stores (MetricsHistoryScalarSpec, ScalarDecimatedRawSpec,
/// GpuFanDecimatedRawSpec, ComponentTempDecimatedRawSpec, RollupHistorySpec,
/// TemperatureBucketSpec, PrivacySessionHistorySpec) - this file only checks
/// the facade wiring.
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
    public void Constructor_CreatesThePrivacyLogFile()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);

        Assert.True(File.Exists(Path.Combine(_dir, "privacy.log")));
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
    public void QueryScalarsDecimated_WithARollupEligibleStep_ReadsFromTheRollupRing()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);
        store.Append(new[] { Scalars(0, cpu: 10), Scalars(1, cpu: 30) }, null);

        var slot = Assert.Single(store.QueryScalarsDecimated(0, 59, stepSeconds: 60));
        Assert.Equal(20, slot.CpuAvg);
    }

    [Fact]
    public void Append_PersistsGpuFanAndComponentTempReadings()
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
        var gpu = Assert.Single(row.Gpus);
        Assert.Equal("gpu-0", gpu.GpuId);
        Assert.Equal(10, gpu.LoadPercent);
        var fan = Assert.Single(row.Fans);
        Assert.Equal("fan-0", fan.FanId);
        Assert.Equal(1000, fan.Rpm);
        var component = Assert.Single(row.ComponentTemps);
        Assert.Equal("ram:0", component.ComponentId);
        Assert.Equal("ram", component.Kind);
        Assert.Equal(40, component.ValueC);
    }

    [Fact]
    public void QueryTemperatureBuckets_RollsUpCpuGpuAndComponentTemps()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);
        var sample = new MetricSample(1_000, null, null, null, null, 55,
            new[] { new GpuReading("gpu-0", "GPU", "", 10, 62) },
            Array.Empty<FanReading>())
        {
            ComponentTemps = new[] { new ComponentTempReading("ram:0", "ram", "DIMM", 38) },
        };

        store.Append(new[] { sample }, null);

        var rows = store.QueryTemperatureBuckets(0, 10_000_000);
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.ComponentId == "cpu" && r.AvgC == 55);
        Assert.Contains(rows, r => r.ComponentId == "gpu:gpu-0" && r.AvgC == 62);
        Assert.Contains(rows, r => r.ComponentId == "ram:0" && r.AvgC == 38);
    }

    [Fact]
    public void GpuAndFanHistory_SurvivesReopen_WithStableRingIndicesAndData()
    {
        var sample = new MetricSample(10, 50, 60, 1000, 500, 55,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 42, 55) },
            new[] { new FanReading("fan-0", "Fan 1", 1200, 60) });

        using (var store = new BinaryMetricsHistoryStore(_dir))
        {
            store.Append(new[] { sample }, null);
        }

        using var reopened = new BinaryMetricsHistoryStore(_dir);
        var row = Assert.Single(reopened.Query(0, 100));

        var gpu = Assert.Single(row.Gpus);
        Assert.Equal("gpu-0", gpu.GpuId);
        Assert.Equal(42, gpu.LoadPercent);
        var fan = Assert.Single(row.Fans);
        Assert.Equal("fan-0", fan.FanId);
        Assert.Equal(1200, fan.Rpm);

        // A newly-seen entity after reopen must land at the NEXT ring index,
        // not collide with gpu-0's already-reopened index 0.
        reopened.Append(new[]
        {
            new MetricSample(11, null, null, null, null, null,
                new[] { new GpuReading("gpu-1", "RX 7900", "", 20, 40) }, Array.Empty<FanReading>()),
        }, null);
        var rows = reopened.Query(0, 100);
        Assert.Contains(rows, r => r.Gpus.Any(g => g.GpuId == "gpu-1" && g.LoadPercent == 20));
        Assert.Contains(rows, r => r.Gpus.Any(g => g.GpuId == "gpu-0" && g.LoadPercent == 42));
    }

    [Fact]
    public void ComponentTempHistory_SurvivesReopen_WithStableRingIndicesAndData()
    {
        var sample = new MetricSample(10, null, null, null, null, null,
            Array.Empty<GpuReading>(), Array.Empty<FanReading>())
        {
            ComponentTemps = new[] { new ComponentTempReading("storage:serial1", "storage", "Samsung 990 Pro", 45) },
        };

        using (var store = new BinaryMetricsHistoryStore(_dir))
        {
            store.Append(new[] { sample }, null);
        }

        using var reopened = new BinaryMetricsHistoryStore(_dir);
        var row = Assert.Single(reopened.Query(0, 100));
        var component = Assert.Single(row.ComponentTemps);
        Assert.Equal("storage:serial1", component.ComponentId);
        Assert.Equal(45, component.ValueC);

        // A newly-seen component after reopen must land at the NEXT ring
        // index, not collide with storage:serial1's already-reopened index 0.
        reopened.Append(new[]
        {
            new MetricSample(11, null, null, null, null, null,
                Array.Empty<GpuReading>(), Array.Empty<FanReading>())
            {
                ComponentTemps = new[] { new ComponentTempReading("ram:0", "ram", "DIMM A2", 38) },
            },
        }, null);
        var rows = reopened.Query(0, 100);
        Assert.Contains(rows, r => r.ComponentTemps.Any(c => c.ComponentId == "ram:0" && c.ValueC == 38));
        Assert.Contains(rows, r => r.ComponentTemps.Any(c => c.ComponentId == "storage:serial1" && c.ValueC == 45));
    }

    [Fact]
    public void ResetAll_ClearsScalarHistory_AndRecordingContinuesAfterwards()
    {
        // Real wall-clock timestamps, not small fixed constants: Append
        // drops any sample older than ResetAll's reset stamp (see
        // BinaryMetricsHistoryStore._resetAtUtcSec), which a hardcoded past
        // ts would always be.
        using var store = new BinaryMetricsHistoryStore(_dir);
        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        store.Append(new[] { Scalars(nowSec, cpu: 50) }, null);

        store.ResetAll();
        Assert.Empty(store.Query(nowSec - 5, nowSec + 200));

        store.Append(new[] { Scalars(nowSec + 100, cpu: 70) }, null);
        var row = Assert.Single(store.Query(nowSec - 5, nowSec + 200));
        Assert.Equal(70, row.CpuPercent);
    }

    [Fact]
    public void BlankFpsSeries_ClearsOnlyFps_LeavingOtherFieldsOnTheSameSlotIntact()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);
        store.Append(new[] { new MetricSample(10, 42, 61, 1000, 500, 55,
            Array.Empty<GpuReading>(), Array.Empty<FanReading>()) { Fps = 60 } }, null);

        var cleared = store.BlankFpsSeries();

        var row = Assert.Single(store.Query(0, 100));
        Assert.Equal(2, cleared); // fps.ring + fps.min, both reset in full
        Assert.Null(row.Fps);
        Assert.Equal(42, row.CpuPercent);
        Assert.Equal(61, row.MemoryPercent);
    }

    [Fact]
    public void ResetAll_ThenALateFlushOfAPreResetSample_IsDropped()
    {
        // Reproduces MetricsSampleBuffer replaying a sample it buffered
        // before ResetAll ran but had not yet flushed - the sample's own ts
        // predates the reset stamp, so Append must silently drop it rather
        // than re-writing history the reset just cleared.
        using var store = new BinaryMetricsHistoryStore(_dir);
        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var staleSample = Scalars(nowSec - 50, cpu: 99);

        store.ResetAll();
        store.Append(new[] { staleSample }, null);

        Assert.Empty(store.Query(nowSec - 100, nowSec + 100));
    }

    [Fact]
    public void BlankFpsSeries_ClearsTheMinuteRollup_SoWideZoomStepsAlsoStopShowingFps()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);
        var minuteFloor = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 * 60;
        store.Append(new[] { new MetricSample(minuteFloor, null, null, null, null, null,
            Array.Empty<GpuReading>(), Array.Empty<FanReading>()) { Fps = 60 } }, null);

        var before60 = store.QueryScalarsDecimated(minuteFloor, minuteFloor + 299, stepSeconds: 60);
        var before300 = store.QueryScalarsDecimated(minuteFloor, minuteFloor + 299, stepSeconds: 300);
        Assert.Equal(60, Assert.Single(before60).FpsAvg);
        Assert.Equal(60, Assert.Single(before300).FpsAvg);

        store.BlankFpsSeries();

        var after60 = store.QueryScalarsDecimated(minuteFloor, minuteFloor + 299, stepSeconds: 60);
        var after300 = store.QueryScalarsDecimated(minuteFloor, minuteFloor + 299, stepSeconds: 300);
        Assert.Null(Assert.Single(after60).FpsAvg);
        Assert.Null(Assert.Single(after300).FpsAvg);
    }
}
