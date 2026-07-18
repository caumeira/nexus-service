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

    private static MetricSample Scalars(
        long ts, double? cpu = 50, double? mem = 60, double? netIn = 1000, double? netOut = 500, double? cpuTemp = 55,
        double? diskRead = 800, double? diskWrite = 400) =>
        new(ts, cpu, mem, netIn, netOut, cpuTemp, Array.Empty<GpuReading>(), Array.Empty<FanReading>(),
            DiskReadBytesPerSec: diskRead, DiskWriteBytesPerSec: diskWrite);

    // Scalar-only Append/Query behavior (round trip, null-vs-zero, same-ts
    // replace, ascending order, empty no-op, prune cutoff) is pinned once in
    // MetricsHistoryScalarSpec, and privacy-session behavior once in
    // PrivacySessionHistorySpec, both run against this store and
    // BinaryMetricsHistoryStore - see those files. What remains here is
    // everything specific to SQLite: GPU/fan entity tables, surrogate-key
    // rollback, key stability across reopen, and disk read/write fields
    // (BinaryMetricsHistoryStore's scalar ring has no slot for them yet).

    [Fact]
    public void Append_then_Query_RoundTripsDiskFields()
    {
        _store.Append(new[] { Scalars(1000, diskRead: 22222, diskWrite: 11111) }, null);

        var row = Assert.Single(_store.Query(0, 10_000));
        Assert.Equal(22222, row.DiskReadBytesPerSec);
        Assert.Equal(11111, row.DiskWriteBytesPerSec);
    }

    [Fact]
    public void Append_preserves_null_disk_fields_as_source_failed_not_zero()
    {
        _store.Append(new[] { Scalars(1000, diskRead: null, diskWrite: null) }, null);

        var row = Assert.Single(_store.Query(0, 10_000));
        Assert.Null(row.DiskReadBytesPerSec);
        Assert.Null(row.DiskWriteBytesPerSec);
    }

    [Fact]
    public void QueryScalarsDecimated_AveragesAndMaxesDiskFieldsWithinASlot()
    {
        _store.Append(new[]
        {
            Scalars(0, cpu: 10, diskRead: 1000, diskWrite: 200),
            Scalars(1, cpu: 10, diskRead: 3000, diskWrite: 600),
        }, null);

        var slot = Assert.Single(_store.QueryScalarsDecimated(0, 1, stepSeconds: 10));

        Assert.Equal(2000, slot.DiskReadAvg);
        Assert.Equal(3000, slot.DiskReadMax);
        Assert.Equal(400, slot.DiskWriteAvg);
        Assert.Equal(600, slot.DiskWriteMax);
    }

    [Fact]
    public void QueryScalarsDecimated_LeavesDiskFieldsNull_WhenEveryReadingInTheSlotWasNull()
    {
        _store.Append(new[] { Scalars(0, cpu: 10, diskRead: null, diskWrite: null) }, null);

        var slot = Assert.Single(_store.QueryScalarsDecimated(0, 0, stepSeconds: 10));

        Assert.Null(slot.DiskReadAvg);
        Assert.Null(slot.DiskReadMax);
        Assert.Null(slot.DiskWriteAvg);
        Assert.Null(slot.DiskWriteMax);
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
}
