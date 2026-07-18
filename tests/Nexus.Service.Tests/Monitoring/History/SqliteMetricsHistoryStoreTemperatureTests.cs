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
/// Covers the one-time import of a legacy temperature.db into
/// SqliteMetricsHistoryStore's temp_buckets table - SQLite-only, since the
/// binary store never shipped a legacy schema to import from (see the
/// binary-metrics-store design notes' phased build). The unified
/// cpu/gpu/storage/ram temperature bucket pipeline itself is covered by
/// TemperatureBucketSpec, parameterized over both stores.
/// </summary>
public class SqliteMetricsHistoryStoreTemperatureTests
{
    // Every test below uses its own isolated directory: opening
    // SqliteMetricsHistoryStore against a directory already boots metrics.db
    // (bumping schema_meta past the import-gate version) before a test
    // method gets a chance to place temperature.db next to it, which would
    // make the import a permanent no-op for the rest of the test.
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

    [Fact]
    public void Constructor_LegacyImportThrows_DoesNotBumpVersion_AndRetriesOnNextBoot()
    {
        var dir = CreateIsolatedDir();
        try
        {
            var legacyPath = Path.Combine(dir, "temperature.db");
            // A valid SQLite file with no temp_buckets table: the import's
            // SELECT throws "no such table", exercising a genuinely failed
            // import without depending on SQLite's malformed-file detection.
            using (var badLegacy = new SqliteConnection($"Data Source={legacyPath}"))
            {
                badLegacy.Open();
                using var cmd = badLegacy.CreateCommand();
                cmd.CommandText = "CREATE TABLE unrelated (x INTEGER);";
                cmd.ExecuteNonQuery();
            }

            var metricsPath = Path.Combine(dir, "metrics.db");
            using (var store = new SqliteMetricsHistoryStore(metricsPath))
            {
                Assert.Empty(store.QueryTemperatureBuckets(0, long.MaxValue));
            }

            // Replace the broken file with a real one: a later boot must
            // retry the import rather than treat the failed attempt as done.
            // Clear Microsoft.Data.Sqlite's connection pool first - otherwise
            // a reopen at this same path can hand back a pooled native handle
            // still attached to the deleted file.
            SqliteConnection.ClearAllPools();
            File.Delete(legacyPath);
            var t0Ms = 1_700_000_000_000L;
            CreateLegacyTemperatureDb(legacyPath, new[] { ("cpu", "cpu", "Legacy CPU", t0Ms, 50.0, 55.0, 10) });

            using var retried = new SqliteMetricsHistoryStore(metricsPath);
            var row = Assert.Single(retried.QueryTemperatureBuckets(t0Ms - 1, t0Ms + 1));
            Assert.Equal("Legacy CPU", row.Name);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Constructor_SkipsLegacyRowsWithNonPositiveSamples_AvoidingDivideByZero()
    {
        var dir = CreateIsolatedDir();
        try
        {
            var t0Ms = 1_700_000_000_000L;
            CreateLegacyTemperatureDb(Path.Combine(dir, "temperature.db"), new[]
            {
                ("cpu", "cpu", "Legacy CPU", t0Ms, 50.0, 55.0, 0),
                ("storage:legacy1", "storage", "Old SSD", t0Ms, 40.0, 42.5, 8),
            });

            using var store = new SqliteMetricsHistoryStore(Path.Combine(dir, "metrics.db"));

            var rows = store.QueryTemperatureBuckets(t0Ms - 1, t0Ms + 1);
            var row = Assert.Single(rows);
            Assert.Equal("storage:legacy1", row.ComponentId);
            Assert.False(double.IsNaN(row.AvgC));
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
