using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Nexus.Service.Diagnostics.Temperature;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// Covers the unified temperature pipeline living on SqliteMetricsHistoryStore:
/// cpu/gpu/storage/ram all rolling into temp_buckets, the 90-day retention
/// staying independent of the 7-day load/net/fan tier, and the one-time
/// import of a legacy temperature.db.
/// </summary>
public class SqliteMetricsHistoryStoreTemperatureTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private SqliteMetricsHistoryStore _store;

    public SqliteMetricsHistoryStoreTemperatureTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-temp-unify-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "metrics.db");
        _store = new SqliteMetricsHistoryStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
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

        _store.Append(new[] { sample }, null);

        var rows = _store.QueryTemperatureBuckets(0, 10_000_000);
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
    public void Append_MultipleTicksInTheSameBucket_AveragesRatherThanReplaces()
    {
        var t0 = new MetricSample(1_000, null, null, null, null, 50,
            Array.Empty<GpuReading>(), Array.Empty<FanReading>());
        var t1 = new MetricSample(1_060, null, null, null, null, 60,
            Array.Empty<GpuReading>(), Array.Empty<FanReading>());

        _store.Append(new[] { t0, t1 }, null);

        var row = Assert.Single(_store.QueryTemperatureBuckets(0, 10_000_000));
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

        _store.Append(new[] { t0, t1 }, null);

        var rows = _store.QueryTemperatureBuckets(0, 1_000_000);
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

        _store.Append(new[]
        {
            new MetricSample(eightDaysAgo, 10, null, null, null, 40, Array.Empty<GpuReading>(), Array.Empty<FanReading>()),
            new MetricSample(ninetyOneDaysAgo, 10, null, null, null, 41, Array.Empty<GpuReading>(), Array.Empty<FanReading>()),
        }, null);

        var sevenDayCutoff = nowSec - MetricsHistory.RetentionDays * 86_400L;
        _store.Append(Array.Empty<MetricSample>(), pruneCutoffSec: sevenDayCutoff);

        // The 7-day tier (metric_seconds) has neither row left - both predate it.
        Assert.Empty(_store.Query(0, nowSec));

        // temp_buckets kept its 90-day tier independently: the 8-day-old
        // reading survives, the 91-day-old one does not.
        var rows = _store.QueryTemperatureBuckets(0, nowSec * 1000);
        var row = Assert.Single(rows);
        Assert.Equal(40.0, row.AvgC, precision: 5);
    }

    // These migration tests use their own isolated directory rather than the
    // class-level _store/_dbPath: that fixture's constructor already boots
    // metrics.db (bumping schema_meta past the import-gate version) before a
    // test method gets a chance to place temperature.db next to it, which
    // would make the import a permanent no-op for the rest of the test.
    [Fact]
    public void Constructor_ImportsLegacyTemperatureDb_IntoTempBuckets()
    {
        var dir = CreateIsolatedDir();
        try
        {
            var t0Ms = 1_700_000_000_000L; // arbitrary, 5-min (300_000ms) aligned
            CreateLegacyTemperatureDb(Path.Combine(dir, "temperature.db"), new[]
            {
                ("cpu", "cpu", "Legacy CPU", t0Ms, 50.0, 55.0, 10),
                ("storage:legacy1", "storage", "Old SSD", t0Ms, 40.0, 42.5, 8),
            });

            using var store = new SqliteMetricsHistoryStore(Path.Combine(dir, "metrics.db"));

            var rows = store.QueryTemperatureBuckets(t0Ms - 1, t0Ms + 1);
            Assert.Equal(2, rows.Count);

            var cpu = rows.Single(r => r.ComponentId == "cpu");
            Assert.Equal("Legacy CPU", cpu.Name);
            Assert.Equal(50.0, cpu.AvgC, precision: 5);
            Assert.Equal(55.0, cpu.MaxC, precision: 5);
            Assert.Equal(10, cpu.Samples);

            var storage = rows.Single(r => r.ComponentId == "storage:legacy1");
            Assert.Equal("Old SSD", storage.Name);
            Assert.Equal(40.0, storage.AvgC, precision: 5);
            Assert.Equal(8, storage.Samples);

            // Route parity: the same BuildTemperatureResponse the live route
            // calls renders imported history exactly like natively-sampled rows.
            var response = DiagnosticsHealthRoutes.BuildTemperatureResponse(rows, tierWidthMinutes: TemperatureInsights.NativeBucketMinutes);
            Assert.True(response.Supported);
            Assert.Equal(2, response.Series.Count);
            Assert.Contains(response.Series, s => s.Id == "cpu" && s.Points.Single().Avg == 50.0);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Constructor_LegacyImport_IsIdempotentAcrossReopens()
    {
        var dir = CreateIsolatedDir();
        try
        {
            var t0Ms = 1_700_000_000_000L;
            CreateLegacyTemperatureDb(Path.Combine(dir, "temperature.db"),
                new[] { ("cpu", "cpu", "Legacy CPU", t0Ms, 50.0, 55.0, 10) });
            var metricsPath = Path.Combine(dir, "metrics.db");

            using (var store = new SqliteMetricsHistoryStore(metricsPath))
            {
                Assert.Single(store.QueryTemperatureBuckets(t0Ms - 1, t0Ms + 1));
            }

            // Reopening must not re-run the import (schema_meta already at the
            // post-import version) - the row must not be duplicated or altered.
            using var reopened = new SqliteMetricsHistoryStore(metricsPath);
            var rows = reopened.QueryTemperatureBuckets(t0Ms - 1, t0Ms + 1);
            var row = Assert.Single(rows);
            Assert.Equal(10, row.Samples);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Constructor_NoLegacyTemperatureDb_ImportsNothingAndDoesNotThrow()
    {
        var dir = CreateIsolatedDir();
        try
        {
            // No temperature.db in dir - a fresh install has nothing to import.
            using var store = new SqliteMetricsHistoryStore(Path.Combine(dir, "metrics.db"));

            Assert.Empty(store.QueryTemperatureBuckets(0, long.MaxValue));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static string CreateIsolatedDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-temp-unify-legacy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CreateLegacyTemperatureDb(
        string path, IEnumerable<(string ComponentId, string Kind, string Name, long BucketUtcMs, double AvgC, double MaxC, int Samples)> rows)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE temp_buckets (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    component_id TEXT    NOT NULL,
                    kind         TEXT    NOT NULL,
                    name         TEXT    NOT NULL,
                    bucket_utc   INTEGER NOT NULL,
                    avg_c        REAL    NOT NULL,
                    max_c        REAL    NOT NULL,
                    samples      INTEGER NOT NULL
                );
            """;
            cmd.ExecuteNonQuery();
        }
        foreach (var row in rows)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO temp_buckets (component_id, kind, name, bucket_utc, avg_c, max_c, samples)
                VALUES ($id, $kind, $name, $bucket, $avg, $max, $samples);
            """;
            cmd.Parameters.AddWithValue("$id", row.ComponentId);
            cmd.Parameters.AddWithValue("$kind", row.Kind);
            cmd.Parameters.AddWithValue("$name", row.Name);
            cmd.Parameters.AddWithValue("$bucket", row.BucketUtcMs);
            cmd.Parameters.AddWithValue("$avg", row.AvgC);
            cmd.Parameters.AddWithValue("$max", row.MaxC);
            cmd.Parameters.AddWithValue("$samples", row.Samples);
            cmd.ExecuteNonQuery();
        }
    }
}
