using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Nexus.Service.Persistence;

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
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly object _writeLock = new();

    public SqliteTemperatureHistoryStore() : this(ResolveDatabasePath()) { }

    public SqliteTemperatureHistoryStore(string dbPath)
    {
        _dbPath = dbPath;
        _connection = SqliteStores.OpenConnection(dbPath);
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

    public IReadOnlyList<string> FindLegacyGpuComponentIds(string name)
    {
        lock (_writeLock)
        {
            var result = new List<string>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT component_id FROM temp_buckets WHERE kind = 'gpu' AND name = $name;";
            cmd.Parameters.AddWithValue("$name", name);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (LegacyGpuComponentId.IsLegacy(id))
                {
                    result.Add(id);
                }
            }
            return result;
        }
    }

    public int RekeyComponent(string oldId, string newId)
    {
        if (oldId == newId)
        {
            return 0;
        }

        lock (_writeLock)
        {
            using var tx = _connection.BeginTransaction();

            using var countCmd = _connection.CreateCommand();
            countCmd.Transaction = tx;
            countCmd.CommandText = "SELECT COUNT(*) FROM temp_buckets WHERE component_id = $old;";
            countCmd.Parameters.AddWithValue("$old", oldId);
            var moved = Convert.ToInt32(countCmd.ExecuteScalar());
            if (moved == 0)
            {
                tx.Commit();
                return 0;
            }

            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE temp_buckets
                SET avg_c = (SELECT o.avg_c FROM temp_buckets o WHERE o.component_id = $old AND o.bucket_utc = temp_buckets.bucket_utc),
                    max_c = (SELECT o.max_c FROM temp_buckets o WHERE o.component_id = $old AND o.bucket_utc = temp_buckets.bucket_utc),
                    samples = (SELECT o.samples FROM temp_buckets o WHERE o.component_id = $old AND o.bucket_utc = temp_buckets.bucket_utc)
                WHERE component_id = $new
                  AND EXISTS (
                    SELECT 1 FROM temp_buckets o WHERE o.component_id = $old AND o.bucket_utc = temp_buckets.bucket_utc
                      AND o.samples > temp_buckets.samples
                  );

                UPDATE temp_buckets
                SET component_id = $new
                WHERE component_id = $old
                  AND bucket_utc NOT IN (SELECT bucket_utc FROM temp_buckets WHERE component_id = $new);

                DELETE FROM temp_buckets WHERE component_id = $old;
            """;
            cmd.Parameters.AddWithValue("$old", oldId);
            cmd.Parameters.AddWithValue("$new", newId);
            cmd.ExecuteNonQuery();

            tx.Commit();
            return moved;
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

    private static string ResolveDatabasePath() =>
        Path.Combine(NexusDataPaths.DatabaseDir(), "temperature.db");
}
