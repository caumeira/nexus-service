using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

public class SqliteMetricsHistoryStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private SqliteMetricsHistoryStore _store;

    public SqliteMetricsHistoryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-metricshistory-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "metrics.db");
        _store = new SqliteMetricsHistoryStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static MetricSample Scalars(long ts, double? cpu = 50, double? mem = 60, double? netIn = 1000, double? netOut = 500, double? cpuTemp = 55) =>
        new(ts, cpu, mem, netIn, netOut, cpuTemp, Array.Empty<GpuReading>(), Array.Empty<FanReading>());

    [Fact]
    public void Append_then_Query_RoundTripsScalarFields()
    {
        _store.Append(new[] { Scalars(1000, cpu: 42.3, mem: 61.7, netIn: 12345, netOut: 6789, cpuTemp: 55.4) }, null);

        var row = Assert.Single(_store.Query(0, 10_000));
        Assert.Equal(1000, row.TsSec);
        Assert.Equal(42.3, row.CpuPercent);
        Assert.Equal(61.7, row.MemoryPercent);
        Assert.Equal(12345, row.NetInBytesPerSec);
        Assert.Equal(6789, row.NetOutBytesPerSec);
        Assert.Equal(55.4, row.CpuTempC);
    }

    [Fact]
    public void Append_preserves_null_fields_as_source_failed_not_zero()
    {
        _store.Append(new[] { Scalars(1000, cpu: null, mem: 60, netIn: null, netOut: null, cpuTemp: null) }, null);

        var row = Assert.Single(_store.Query(0, 10_000));
        Assert.Null(row.CpuPercent);
        Assert.Equal(60, row.MemoryPercent);
        Assert.Null(row.NetInBytesPerSec);
        Assert.Null(row.NetOutBytesPerSec);
        Assert.Null(row.CpuTempC);
    }

    [Fact]
    public void Append_SameTimestamp_ReplacesRatherThanDuplicates()
    {
        _store.Append(new[] { Scalars(1000, cpu: 10) }, null);
        _store.Append(new[] { Scalars(1000, cpu: 90) }, null);

        var row = Assert.Single(_store.Query(0, 10_000));
        Assert.Equal(90, row.CpuPercent);
    }

    [Fact]
    public void Query_ReturnsRowsAscendingByTs_AndExcludesOutOfRange()
    {
        _store.Append(new[] { Scalars(3000), Scalars(1000), Scalars(9000), Scalars(2000) }, null);

        var rows = _store.Query(1500, 3500);

        Assert.Equal(new long[] { 2000, 3000 }, rows.Select(r => r.TsSec).ToArray());
    }

    [Fact]
    public void Append_RoundTripsGpuReadings_KeyedByEntity()
    {
        var sample = new MetricSample(1000, 10, 20, null, null, null,
            new[]
            {
                new GpuReading("gpu-nvidia-0", "RTX 5080", "abc:def", 55.5, 62.1),
                new GpuReading("gpu-amd-0", "RX 7900", "", 30.0, 45.0),
            },
            Array.Empty<FanReading>());

        _store.Append(new[] { sample }, null);

        var row = Assert.Single(_store.Query(0, 10_000));
        Assert.Equal(2, row.Gpus.Count);
        var nvidia = row.Gpus.Single(g => g.GpuId == "gpu-nvidia-0");
        Assert.Equal("RTX 5080", nvidia.Name);
        Assert.Equal(55.5, nvidia.LoadPercent);
        Assert.Equal(62.1, nvidia.TempC);
        var amd = row.Gpus.Single(g => g.GpuId == "gpu-amd-0");
        Assert.Equal(30.0, amd.LoadPercent);
    }

    [Fact]
    public void Append_RoundTripsFanReadings_KeyedByEntity()
    {
        var sample = new MetricSample(1000, null, null, null, null, null,
            Array.Empty<GpuReading>(),
            new[]
            {
                new FanReading("fan-0", "Fan 1", 1200, 45),
                new FanReading("fan-1", "Fan 2", null, 100), // Rpm unavailable
            });

        _store.Append(new[] { sample }, null);

        var row = Assert.Single(_store.Query(0, 10_000));
        Assert.Equal(2, row.Fans.Count);
        var fan0 = row.Fans.Single(f => f.FanId == "fan-0");
        Assert.Equal(1200, fan0.Rpm);
        Assert.Equal(45, fan0.Duty);
        var fan1 = row.Fans.Single(f => f.FanId == "fan-1");
        Assert.Null(fan1.Rpm);
        Assert.Equal(100, fan1.Duty);
    }

    [Fact]
    public void Append_MultipleTicksForSameGpu_AccumulatesPerSecondRows_NotDuplicateEntities()
    {
        var s1 = new MetricSample(1000, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 10, 40) }, Array.Empty<FanReading>());
        var s2 = new MetricSample(1001, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 20, 45) }, Array.Empty<FanReading>());

        _store.Append(new[] { s1, s2 }, null);

        var rows = _store.Query(0, 10_000);
        Assert.Equal(2, rows.Count);
        Assert.Equal(10, rows[0].Gpus.Single().LoadPercent);
        Assert.Equal(20, rows[1].Gpus.Single().LoadPercent);
    }

    [Fact]
    public void Append_WithPruneCutoff_DeletesOlderScalarAndEntityRows()
    {
        var old = new MetricSample(1000, 10, null, null, null, null,
            new[] { new GpuReading("gpu-0", "GPU", "", 5, 30) },
            new[] { new FanReading("fan-0", "Fan", 1000, 50) });
        var recent = new MetricSample(5000, 20, null, null, null, null,
            new[] { new GpuReading("gpu-0", "GPU", "", 6, 31) },
            new[] { new FanReading("fan-0", "Fan", 1100, 55) });
        _store.Append(new[] { old, recent }, null);

        _store.Append(Array.Empty<MetricSample>(), pruneCutoffSec: 3000);

        var rows = _store.Query(0, 10_000);
        var row = Assert.Single(rows);
        Assert.Equal(5000, row.TsSec);
        Assert.Single(row.Gpus);
        Assert.Single(row.Fans);
    }

    [Fact]
    public void KeySeries_key_stability_across_reopen()
    {
        var sample = new MetricSample(1000, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 50, 60) },
            new[] { new FanReading("fan-0", "Fan 1", 1200, 45) });
        _store.Append(new[] { sample }, null);
        _store.Dispose();

        _store = new SqliteMetricsHistoryStore(_dbPath);
        var sampleAfterReopen = new MetricSample(2000, null, null, null, null, null,
            new[] { new GpuReading("gpu-0", "RTX 5080", "", 55, 61) },
            new[] { new FanReading("fan-0", "Fan 1", 1250, 50) });
        _store.Append(new[] { sampleAfterReopen }, null);

        var rows = _store.Query(0, 10_000);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("gpu-0", r.Gpus.Single().GpuId));
        Assert.All(rows, r => Assert.Equal("fan-0", r.Fans.Single().FanId));
    }

    [Fact]
    public void Append_RollsBackSurrogateKeyResolution_WhenTheTransactionFails()
    {
        // fan-a resolves a real surrogate key before fan-b's NULL name (not
        // null in the FanReading record itself, but forced null! here) hits
        // fan_series.name's NOT NULL constraint and aborts the whole
        // transaction, including fan-a's otherwise-valid insert.
        var failingSample = new MetricSample(0, null, null, null, null, null,
            Array.Empty<GpuReading>(),
            new[]
            {
                new FanReading("fan-a", "Fan A", 1000, 50),
                new FanReading("fan-b", null!, 1100, 60),
            });

        Assert.ThrowsAny<Exception>(() => _store.Append(new[] { failingSample }, null));

        // A cache entry surviving the rollback would point fan-a's key at a
        // fan_series row that no longer exists; a subsequent Append must
        // resolve a fresh, working key instead of reusing a dangling one.
        var retrySample = new MetricSample(1, null, null, null, null, null,
            Array.Empty<GpuReading>(),
            new[] { new FanReading("fan-a", "Fan A", 1200, 70) });
        _store.Append(new[] { retrySample }, null);

        var row = Assert.Single(_store.Query(0, 10_000));
        Assert.Equal(1, row.TsSec);
        var fan = Assert.Single(row.Fans);
        Assert.Equal("fan-a", fan.FanId);
        Assert.Equal(1200, fan.Rpm);
    }

    [Fact]
    public void Append_EmptyList_WithNoCutoff_IsANoOp()
    {
        _store.Append(Array.Empty<MetricSample>(), null);

        Assert.Empty(_store.Query(0, long.MaxValue));
    }

    private IPrivacySessionStore PrivacyStore => _store;

    [Fact]
    public void PrivacyUpsert_ThenQuery_RoundTripsAnOpenSession()
    {
        _store.Upsert("microphone", "app.exe", 1000, null);

        var row = Assert.Single(PrivacyStore.Query(0, 10_000));
        Assert.Equal("app.exe", row.AppId);
        Assert.Equal("microphone", row.Capability);
        Assert.Equal(1000, row.StartUtcSec);
        Assert.Null(row.EndUtcSec);
    }

    [Fact]
    public void PrivacyUpsert_SameAppCapabilityStart_ClosesTheOpenSessionInPlace()
    {
        _store.Upsert("microphone", "app.exe", 1000, null);

        _store.Upsert("microphone", "app.exe", 1000, 1080);

        var row = Assert.Single(PrivacyStore.Query(0, 10_000));
        Assert.Equal(1000, row.StartUtcSec);
        Assert.Equal(1080, row.EndUtcSec);
    }

    [Fact]
    public void PrivacyUpsert_DifferentStart_IsASeparateSession()
    {
        _store.Upsert("microphone", "app.exe", 1000, 1080);
        _store.Upsert("microphone", "app.exe", 2000, null);

        var rows = PrivacyStore.Query(0, 10_000);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void PrivacyQuery_ExcludesSessionsOutsideTheWindow()
    {
        _store.Upsert("microphone", "app.exe", 1000, 1080);
        _store.Upsert("microphone", "app.exe", 5000, 5080);

        var rows = PrivacyStore.Query(2000, 4000);

        Assert.Empty(rows);
    }

    [Fact]
    public void PrivacyQuery_IncludesAnOpenSession_EvenWhenItStartedBeforeTheWindow()
    {
        _store.Upsert("microphone", "app.exe", 100, null);

        var rows = PrivacyStore.Query(5000, 10_000);

        var row = Assert.Single(rows);
        Assert.Null(row.EndUtcSec);
    }

    [Fact]
    public void PrivacyQuery_ExcludesAnOpenSession_WhenItStartsAfterTheWindow()
    {
        _store.Upsert("microphone", "app.exe", 20_000, null);

        var rows = PrivacyStore.Query(0, 10_000);

        Assert.Empty(rows);
    }

    [Fact]
    public void PrivacyPrune_DeletesOnlyClosedSessionsEndingBeforeTheCutoff()
    {
        _store.Upsert("microphone", "app.exe", 1000, 1080);   // closed, old -> pruned
        _store.Upsert("microphone", "app2.exe", 5000, 5080);  // closed, recent -> kept
        _store.Upsert("microphone", "app3.exe", 500, null);   // open, old start -> kept regardless

        PrivacyStore.PruneOlderThan(2000);

        var rows = PrivacyStore.Query(0, long.MaxValue);
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.AppId == "app.exe");
        Assert.Contains(rows, r => r.AppId == "app2.exe");
        Assert.Contains(rows, r => r.AppId == "app3.exe" && r.EndUtcSec == null);
    }

    [Fact]
    public void PrivacySessions_SurviveReopen()
    {
        _store.Upsert("webcam", "app.exe", 1000, 1080);
        _store.Dispose();

        _store = new SqliteMetricsHistoryStore(_dbPath);

        var row = Assert.Single(PrivacyStore.Query(0, 10_000));
        Assert.Equal("webcam", row.Capability);
        Assert.Equal(1080, row.EndUtcSec);
    }
}
