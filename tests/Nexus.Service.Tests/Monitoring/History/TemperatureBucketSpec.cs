using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// The unified 90-day temperature bucket rollup (temp_buckets in SQLite,
/// TempBucketStore rebuilt from the scalar/gpu/temp-component rings in the
/// binary store): cpu/gpu/storage/ram all rolling into one query, the
/// 90-day retention staying independent of the 7-day load/net/fan tier, and
/// the source-retention guard that stops a backward-dated replay from
/// clobbering an aged-out bucket's retained aggregate. Run against both
/// SqliteMetricsHistoryStore (SqliteTemperatureBucketSpecTests) and
/// BinaryMetricsHistoryStore (BinaryTemperatureBucketSpecTests) - the
/// legacy temperature.db import is SQLite-only (no binary-store
/// equivalent) and stays pinned to SqliteMetricsHistoryStoreTemperatureTests.
/// </summary>
public abstract class TemperatureBucketSpec : IDisposable
{
    private readonly string _dir;
    protected IMetricsHistoryStore Store;

    protected TemperatureBucketSpec()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-tempbucket-spec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        Store = CreateStore(_dir);
    }

    protected abstract IMetricsHistoryStore CreateStore(string dir);

    public virtual void Dispose()
    {
        Store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Append_RollsUpCpuGpuStorageRamTemps_IntoOneUnifiedTemperatureQuery()
    {
        var sample = new MetricSample(
            1_000, 50, 60, null, null, 55.5,
            new[] { new GpuReading("gpu-nvidia-0", "RTX 5080", "", 40, 62.3) },
            Array.Empty<FanReading>(),
            CpuName: "Test CPU",
            ComponentTemps: new[]
            {
                new ComponentTempReading("storage:serial1", "storage", "Samsung 990 Pro", 45.0),
                new ComponentTempReading("ram:0", "ram", "DIMM_A1", 38.0),
            });

        Store.Append(new[] { sample }, null);

        var rows = Store.QueryTemperatureBuckets(0, 10_000_000);
        Assert.Equal(4, rows.Count);

        var cpu = rows.Single(r => r.ComponentId == "cpu");
        Assert.Equal("cpu", cpu.Kind);
        Assert.Equal("Test CPU", cpu.Name);
        Assert.Equal(55.5, cpu.AvgC, precision: 5);
        Assert.Equal(1, cpu.Samples);

        var gpu = rows.Single(r => r.ComponentId == "gpu:gpu-nvidia-0");
        Assert.Equal("gpu", gpu.Kind);
        Assert.Equal("RTX 5080", gpu.Name);
        Assert.Equal(62.3, gpu.AvgC, precision: 5);

        var storage = rows.Single(r => r.ComponentId == "storage:serial1");
        Assert.Equal("storage", storage.Kind);
        Assert.Equal(45.0, storage.AvgC, precision: 5);

        var ram = rows.Single(r => r.ComponentId == "ram:0");
        Assert.Equal("ram", ram.Kind);
        Assert.Equal(38.0, ram.AvgC, precision: 5);
    }

    [Fact]
    public void Append_then_Query_RoundTripsComponentTemps_KeyedByEntity()
    {
        var sample = new MetricSample(1000, null, null, null, null, null,
            Array.Empty<GpuReading>(), Array.Empty<FanReading>(),
            ComponentTemps: new[]
            {
                new ComponentTempReading("storage:serial1", "storage", "Samsung 990 Pro", 45.0),
                new ComponentTempReading("ram:0", "ram", "DIMM_A1", 38.0),
            });

        Store.Append(new[] { sample }, null);

        var row = Assert.Single(Store.Query(0, 10_000));
        Assert.Equal(2, row.ComponentTemps.Count);
        var storage = row.ComponentTemps.Single(c => c.ComponentId == "storage:serial1");
        Assert.Equal("storage", storage.Kind);
        Assert.Equal("Samsung 990 Pro", storage.Name);
        Assert.Equal(45.0, storage.ValueC);
        var ram = row.ComponentTemps.Single(c => c.ComponentId == "ram:0");
        Assert.Equal("ram", ram.Kind);
        Assert.Equal(38.0, ram.ValueC);
    }

    // Store->route seam: Query must populate ComponentTemps for every row in
    // the window (not just the newest), or the narrow-window route path
    // (no buffer tail here) renders drive-temp/mem-temp from only whatever a
    // buffer tail happens to carry.
    [Fact]
    public void BuildHistoryResponse_NarrowPath_DriveTempSeries_SpansTheFullWindow_NotJustTheNewestReading()
    {
        var t0 = new MetricSample(0, null, null, null, null, null,
            Array.Empty<GpuReading>(), Array.Empty<FanReading>(),
            ComponentTemps: new[] { new ComponentTempReading("storage:serial1", "storage", "Samsung 990 Pro", 40.0) });
        var t1 = new MetricSample(30, null, null, null, null, null,
            Array.Empty<GpuReading>(), Array.Empty<FanReading>(),
            ComponentTemps: new[] { new ComponentTempReading("storage:serial1", "storage", "Samsung 990 Pro", 50.0) });
        Store.Append(new[] { t0, t1 }, null);

        var dbSamples = Store.Query(0, 30);
        var response = MonitoringHistoryRoutes.BuildHistoryResponse(
            dbSamples, Array.Empty<MetricSample>(), 0, 30, maxPoints: 600,
            new HashSet<string> { "drive-temp" }, new Dictionary<string, string>());

        var drive = Assert.Single(response.Series);
        Assert.Equal("drive-temp:storage:serial1", drive.Id);
        Assert.Equal(2, drive.Points.Count);
        Assert.Equal(40.0, drive.Points[0].Avg);
        Assert.Equal(50.0, drive.Points[1].Avg);
    }

    [Fact]
    public void Append_MultipleTicksInTheSameBucket_AveragesRatherThanReplaces()
    {
        var t0 = new MetricSample(1_000, null, null, null, null, 50,
            Array.Empty<GpuReading>(), Array.Empty<FanReading>());
        var t1 = new MetricSample(1_060, null, null, null, null, 60,
            Array.Empty<GpuReading>(), Array.Empty<FanReading>());

        Store.Append(new[] { t0, t1 }, null);

        var row = Assert.Single(Store.QueryTemperatureBuckets(0, 10_000_000));
        Assert.Equal("cpu", row.ComponentId);
        Assert.Equal(55.0, row.AvgC, precision: 5);
        Assert.Equal(60.0, row.MaxC, precision: 5);
        Assert.Equal(2, row.Samples);
    }

    [Fact]
    public void Append_ReadingsFiveMinutesApart_LandInDifferentBuckets()
    {
        var t0 = new MetricSample(0, null, null, null, null, 50,
            Array.Empty<GpuReading>(), Array.Empty<FanReading>());
        var t1 = new MetricSample(300, null, null, null, null, 70,
            Array.Empty<GpuReading>(), Array.Empty<FanReading>());

        Store.Append(new[] { t0, t1 }, null);

        var rows = Store.QueryTemperatureBuckets(0, 1_000_000);
        Assert.Equal(2, rows.Count);
        Assert.Equal(0L, rows[0].BucketUtcMs);
        Assert.Equal(300_000L, rows[1].BucketUtcMs);
    }

    [Fact]
    public void Prune_KeepsTempBucketsForNinetyDays_WhileLoadDataPrunesAtSevenDays()
    {
        const long nowSec = 200_000_000L;
        const long eightDaysAgo = nowSec - 8 * 86_400L;   // past the 7-day tier, inside the 90-day temp tier
        const long ninetyOneDaysAgo = nowSec - 91 * 86_400L; // past both

        Store.Append(new[]
        {
            new MetricSample(eightDaysAgo, 10, null, null, null, 40, Array.Empty<GpuReading>(), Array.Empty<FanReading>()),
            new MetricSample(ninetyOneDaysAgo, 10, null, null, null, 41, Array.Empty<GpuReading>(), Array.Empty<FanReading>()),
        }, null);

        var sevenDayCutoff = nowSec - MetricsHistory.RetentionDays * 86_400L;
        Store.Append(Array.Empty<MetricSample>(), pruneCutoffSec: sevenDayCutoff);

        // The 7-day tier has neither row left - both predate it.
        Assert.Empty(Store.Query(0, nowSec));

        // The 90-day temp tier kept its window independently: the 8-day-old
        // reading survives, the 91-day-old one does not.
        var rows = Store.QueryTemperatureBuckets(0, nowSec * 1000);
        var row = Assert.Single(rows);
        Assert.Equal(40.0, row.AvgC, precision: 5);
    }

    [Fact]
    public void Append_BackwardDatedReplayIntoAnAgedOutBucket_DoesNotClobberTheRetainedAggregate()
    {
        const long bucketStart = 1_000_000_200L; // 5-min (300s) aligned
        Assert.Equal(0, bucketStart % 300);

        // Original reading lands while the bucket is inside source retention.
        Store.Append(new[]
        {
            new MetricSample(bucketStart, null, null, null, null, 40,
                Array.Empty<GpuReading>(), Array.Empty<FanReading>()),
        }, null);
        var before = Assert.Single(Store.QueryTemperatureBuckets(bucketStart * 1000, bucketStart * 1000));
        Assert.Equal(40.0, before.AvgC, precision: 5);
        Assert.Equal(1, before.Samples);

        // An 8-day-later prune moves the source floor past the bucket,
        // deleting its raw row (the bucket's own aggregate is still well
        // inside the 90-day tier and survives).
        var pruneCutoff = bucketStart + 8 * 86_400L;
        Store.Append(Array.Empty<MetricSample>(), pruneCutoffSec: pruneCutoff);
        Assert.Empty(Store.Query(bucketStart, bucketStart));

        // A stray reading lands back in the now-aged-out bucket (backward
        // clock step / replay) in the same flush as a normal current-time
        // reading.
        Store.Append(new[]
        {
            new MetricSample(bucketStart + 30, null, null, null, null, 99,
                Array.Empty<GpuReading>(), Array.Empty<FanReading>()),
            new MetricSample(pruneCutoff + 10_000, null, null, null, null, 55,
                Array.Empty<GpuReading>(), Array.Empty<FanReading>()),
        }, null);

        var after = Assert.Single(Store.QueryTemperatureBuckets(bucketStart * 1000, bucketStart * 1000));
        Assert.Equal(40.0, after.AvgC, precision: 5);
        Assert.Equal(1, after.Samples);
    }
}

public sealed class SqliteTemperatureBucketSpecTests : TemperatureBucketSpec
{
    protected override IMetricsHistoryStore CreateStore(string dir) =>
        new SqliteMetricsHistoryStore(Path.Combine(dir, "metrics.db"));
}

public sealed class BinaryTemperatureBucketSpecTests : TemperatureBucketSpec
{
    protected override IMetricsHistoryStore CreateStore(string dir) =>
        new BinaryMetricsHistoryStore(dir);
}
