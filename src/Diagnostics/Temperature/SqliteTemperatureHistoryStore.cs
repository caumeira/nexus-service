using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Nexus.Service.Diagnostics.Temperature;

/// <summary>
/// SQLite-backed persistent store for temperature history, mirroring
/// SqliteScreenTimeStore's connection/pragma/schema conventions. One
/// long-lived connection, one lock serializing reads and writes, WAL mode
/// for durability without blocking the sampler's periodic writes against
/// concurrent API reads.
/// </summary>
public sealed class SqliteTemperatureHistoryStore : ITemperatureHistoryStore
{
    private static readonly object InitLock = new();
    private static bool _sqliteInitialized;

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly object _writeLock = new();

    public SqliteTemperatureHistoryStore() : this(ResolveDatabasePath()) { }

    public SqliteTemperatureHistoryStore(string dbPath)
    {
        _dbPath = dbPath;
        EnsureSqliteBundleInitialized();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        ApplyPragmas();
        EnsureSchema();
    }

    public string DatabasePath => _dbPath;

    public void UpsertBuckets(IReadOnlyList<TemperatureBucketRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        lock (_writeLock)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO temp_buckets (component_id, kind, name, bucket_utc, avg_c, max_c, samples)
                VALUES ($cid, $kind, $name, $bucket, $avg, $max, $samples)
                ON CONFLICT(component_id, bucket_utc) DO UPDATE SET
                    kind = excluded.kind,
                    name = excluded.name,
                    avg_c = excluded.avg_c,
                    max_c = excluded.max_c,
                    samples = excluded.samples;
            """;
            var pCid = cmd.CreateParameter();
            pCid.ParameterName = "$cid";
            cmd.Parameters.Add(pCid);
            var pKind = cmd.CreateParameter();
            pKind.ParameterName = "$kind";
            cmd.Parameters.Add(pKind);
            var pName = cmd.CreateParameter();
            pName.ParameterName = "$name";
            cmd.Parameters.Add(pName);
            var pBucket = cmd.CreateParameter();
            pBucket.ParameterName = "$bucket";
            cmd.Parameters.Add(pBucket);
            var pAvg = cmd.CreateParameter();
            pAvg.ParameterName = "$avg";
            cmd.Parameters.Add(pAvg);
            var pMax = cmd.CreateParameter();
            pMax.ParameterName = "$max";
            cmd.Parameters.Add(pMax);
            var pSamples = cmd.CreateParameter();
            pSamples.ParameterName = "$samples";
            cmd.Parameters.Add(pSamples);

            foreach (var row in rows)
            {
                pCid.Value = row.ComponentId;
                pKind.Value = row.Kind;
                pName.Value = row.Name;
                pBucket.Value = row.BucketUtcMs;
                pAvg.Value = row.AvgC;
                pMax.Value = row.MaxC;
                pSamples.Value = row.Samples;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public IReadOnlyList<TemperatureBucketRow> Query(long fromUtcMs, long toUtcMs)
    {
        lock (_writeLock)
        {
            var result = new List<TemperatureBucketRow>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT component_id, kind, name, bucket_utc, avg_c, max_c, samples
                FROM temp_buckets
                WHERE bucket_utc BETWEEN $from AND $to
                ORDER BY bucket_utc ASC;
            """;
            cmd.Parameters.AddWithValue("$from", fromUtcMs);
            cmd.Parameters.AddWithValue("$to", toUtcMs);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new TemperatureBucketRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5),
                    reader.GetInt32(6)));
            }
            return result;
        }
    }

    public int PruneOlderThan(long utcMs)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM temp_buckets WHERE bucket_utc < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", utcMs);
            return cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        try
        {
            _connection.Close();
            _connection.Dispose();
        }
        catch { }
    }

    private void ApplyPragmas()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=OFF;
        """;
        cmd.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS temp_buckets (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                component_id TEXT    NOT NULL,
                kind         TEXT    NOT NULL,
                name         TEXT    NOT NULL,
                bucket_utc   INTEGER NOT NULL,
                avg_c        REAL    NOT NULL,
                max_c        REAL    NOT NULL,
                samples      INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_temp_buckets_component_bucket ON temp_buckets(component_id, bucket_utc);
            CREATE INDEX IF NOT EXISTS ix_temp_buckets_bucket ON temp_buckets(bucket_utc);

            CREATE TABLE IF NOT EXISTS schema_meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT OR IGNORE INTO schema_meta (key, value) VALUES ('version', '1');
        """;
        cmd.ExecuteNonQuery();
    }

    private static void EnsureSqliteBundleInitialized()
    {
        if (_sqliteInitialized)
        {
            return;
        }
        lock (InitLock)
        {
            if (_sqliteInitialized)
            {
                return;
            }
            SQLitePCL.Batteries_V2.Init();
            _sqliteInitialized = true;
        }
    }

    private static string ResolveDatabasePath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "Nexus", "temperature.db");
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Machine-scope DB: the service runs as LocalSystem, so user-scoped
            // LocalApplicationData would resolve under system32\config\systemprofile.
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Nexus", "temperature.db");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(xdg))
        {
            xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return Path.Combine(xdg, "Nexus", "temperature.db");
    }
}
