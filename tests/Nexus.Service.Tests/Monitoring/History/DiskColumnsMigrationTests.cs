using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// A pre-disk-metrics database's metric_seconds/metric_minutes have no
/// disk_read_bps/disk_write_bps (or their rollup sum/cnt/max counterparts).
/// Reopening that exact legacy shape with the current store must add the
/// columns without throwing (a naive ALTER TABLE ADD COLUMN gated only on
/// schema version would collide with a fresh database's CREATE TABLE, which
/// already defines them) and keep pre-existing rows intact.
/// </summary>
public class DiskColumnsMigrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public DiskColumnsMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-diskcolumnsmigration-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "metrics.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Verbatim pre-disk-metrics DDL: no disk_read_bps/disk_write_bps and no
    // disk rollup columns.
    private void CreateLegacyDatabase()
    {
        using var connection = SqliteStores.OpenConnection(_dbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE metric_seconds (
                ts           INTEGER PRIMARY KEY,
                cpu_x10      INTEGER,
                mem_x10      INTEGER,
                net_in_bps   INTEGER,
                net_out_bps  INTEGER,
                cpu_temp_x10 INTEGER
            );
            CREATE TABLE metric_minutes (
                ts_min           INTEGER PRIMARY KEY,
                cpu_sum_x10      INTEGER, cpu_cnt INTEGER NOT NULL DEFAULT 0, cpu_max_x10 INTEGER,
                mem_sum_x10      INTEGER, mem_cnt INTEGER NOT NULL DEFAULT 0, mem_max_x10 INTEGER,
                net_in_sum       INTEGER, net_in_cnt INTEGER NOT NULL DEFAULT 0, net_in_max INTEGER,
                net_out_sum      INTEGER, net_out_cnt INTEGER NOT NULL DEFAULT 0, net_out_max INTEGER,
                cpu_temp_sum_x10 INTEGER, cpu_temp_cnt INTEGER NOT NULL DEFAULT 0, cpu_temp_max_x10 INTEGER
            );
            CREATE TABLE schema_meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT INTO schema_meta (key, value) VALUES ('version', '3');

            INSERT INTO metric_seconds (ts, cpu_x10, mem_x10, net_in_bps, net_out_bps, cpu_temp_x10)
            VALUES (1000, 500, 600, 1000, 500, 550);
        """;
        cmd.ExecuteNonQuery();
    }

    private string ReadSchemaVersion()
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM schema_meta WHERE key = 'version';";
        return (string)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void Reopening_AddsDiskColumns_WithoutThrowing_AndKeepsExistingRows()
    {
        CreateLegacyDatabase();

        using var store = new SqliteMetricsHistoryStore(_dbPath);

        var row = Assert.Single(store.Query(0, 10_000));
        Assert.Equal(1000, row.TsSec);
        Assert.Equal(50, row.CpuPercent);
        Assert.Null(row.DiskReadBytesPerSec);
        Assert.Null(row.DiskWriteBytesPerSec);
    }

    [Fact]
    public void Reopening_ThenAppending_RoundTripsNewDiskData()
    {
        CreateLegacyDatabase();

        using var store = new SqliteMetricsHistoryStore(_dbPath);
        store.Append(new[]
        {
            new MetricSample(2000, 10, 20, null, null, null, Array.Empty<GpuReading>(), Array.Empty<FanReading>(),
                DiskReadBytesPerSec: 4000, DiskWriteBytesPerSec: 2000),
        }, null);

        var row = store.Query(2000, 2000);
        var sample = Assert.Single(row);
        Assert.Equal(4000, sample.DiskReadBytesPerSec);
        Assert.Equal(2000, sample.DiskWriteBytesPerSec);
    }

    [Fact]
    public void Reopening_Twice_StaysIdempotent_AndDoesNotBumpTheSharedSchemaVersion()
    {
        CreateLegacyDatabase();

        using (var first = new SqliteMetricsHistoryStore(_dbPath))
        {
            Assert.Single(first.Query(0, 10_000));
        }

        // The disk-columns migration must not advance the schema_meta
        // version counter the OTHER migrations in this file gate on
        // (see ImportLegacyTemperatureHistory's retry-on-failure contract) -
        // it stays at the '3' CreateLegacyDatabase seeded, not just unchanged
        // across reopens.
        Assert.Equal("3", ReadSchemaVersion());

        // A second reopen must not re-attempt ALTER TABLE ADD COLUMN against
        // columns the first reopen already added (ColumnExists guards each
        // one).
        using var second = new SqliteMetricsHistoryStore(_dbPath);
        Assert.Single(second.Query(0, 10_000));
        Assert.Equal("3", ReadSchemaVersion());
    }
}
