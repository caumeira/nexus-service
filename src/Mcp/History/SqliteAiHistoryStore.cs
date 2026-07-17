using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Nexus.Service.Persistence;

namespace Nexus.Service.Mcp.History;

/// <summary>
/// SQLite-backed implementation of <see cref="IAiHistoryStore"/>, mirroring
/// SqliteMetricsHistoryStore's connection/pragma/schema conventions in its
/// own database file (ai-history.db). One long-lived connection, one lock
/// serializing every read and write, WAL mode.
///
/// Raw samples roll up into 1-minute then 5-minute aggregates on write: each
/// RecordSamples call inserts the tick's raw rows, then - only when a bucket
/// has fully closed since the last rollup - aggregates completed raw buckets
/// into samples_1m and completed 1-minute buckets into samples_5m, then prunes
/// every tier past its retention window. sensor_meta tracks each sensor's
/// display name/unit/kind and latest reading independent of tier pruning, so
/// KnownSensorIds and the summary tool's Latest field work even when the
/// requested window falls entirely inside an already-pruned tier.
/// </summary>
public sealed class SqliteAiHistoryStore : IAiHistoryStore
{
    private static readonly object InitLock = new();
    private static bool _sqliteInitialized;

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly object _writeLock = new();

    public SqliteAiHistoryStore() : this(ResolveDatabasePath()) { }

    public SqliteAiHistoryStore(string dbPath)
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
    public bool IsAvailable => true;

    public void RecordSamples(IReadOnlyList<AiHistorySampleRow> rows, DateTime nowUtc)
    {
        if (rows.Count == 0)
        {
            return;
        }
        var nowUtcMs = new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds();

        lock (_writeLock)
        {
            using var tx = _connection.BeginTransaction();

            using var rawCmd = _connection.CreateCommand();
            rawCmd.Transaction = tx;
            rawCmd.CommandText = """
                INSERT OR REPLACE INTO samples_raw (sensor_id, name, kind, unit, ts_utc, value)
                VALUES ($sid, $name, $kind, $unit, $ts, $value);
            """;
            var pSid = rawCmd.CreateParameter(); pSid.ParameterName = "$sid"; rawCmd.Parameters.Add(pSid);
            var pName = rawCmd.CreateParameter(); pName.ParameterName = "$name"; rawCmd.Parameters.Add(pName);
            var pKind = rawCmd.CreateParameter(); pKind.ParameterName = "$kind"; rawCmd.Parameters.Add(pKind);
            var pUnit = rawCmd.CreateParameter(); pUnit.ParameterName = "$unit"; rawCmd.Parameters.Add(pUnit);
            var pTs = rawCmd.CreateParameter(); pTs.ParameterName = "$ts"; rawCmd.Parameters.Add(pTs);
            var pValue = rawCmd.CreateParameter(); pValue.ParameterName = "$value"; rawCmd.Parameters.Add(pValue);

            using var metaCmd = _connection.CreateCommand();
            metaCmd.Transaction = tx;
            metaCmd.CommandText = """
                INSERT INTO sensor_meta (sensor_id, name, kind, unit, last_value, last_seen_utc)
                VALUES ($sid, $name, $kind, $unit, $value, $ts)
                ON CONFLICT(sensor_id) DO UPDATE SET
                    name = excluded.name,
                    kind = excluded.kind,
                    unit = excluded.unit,
                    last_value = excluded.last_value,
                    last_seen_utc = excluded.last_seen_utc;
            """;
            var mSid = metaCmd.CreateParameter(); mSid.ParameterName = "$sid"; metaCmd.Parameters.Add(mSid);
            var mName = metaCmd.CreateParameter(); mName.ParameterName = "$name"; metaCmd.Parameters.Add(mName);
            var mKind = metaCmd.CreateParameter(); mKind.ParameterName = "$kind"; metaCmd.Parameters.Add(mKind);
            var mUnit = metaCmd.CreateParameter(); mUnit.ParameterName = "$unit"; metaCmd.Parameters.Add(mUnit);
            var mValue = metaCmd.CreateParameter(); mValue.ParameterName = "$value"; metaCmd.Parameters.Add(mValue);
            var mTs = metaCmd.CreateParameter(); mTs.ParameterName = "$ts"; metaCmd.Parameters.Add(mTs);

            foreach (var row in rows)
            {
                pSid.Value = row.SensorId;
                pName.Value = row.Name;
                pKind.Value = row.Kind;
                pUnit.Value = row.Unit;
                pTs.Value = row.TsUtcMs;
                pValue.Value = row.Value;
                rawCmd.ExecuteNonQuery();

                mSid.Value = row.SensorId;
                mName.Value = row.Name;
                mKind.Value = row.Kind;
                mUnit.Value = row.Unit;
                mValue.Value = row.Value;
                mTs.Value = row.TsUtcMs;
                metaCmd.ExecuteNonQuery();
            }

            RollupIfDue(tx, nowUtcMs);
            PruneOld(tx, nowUtcMs);

            tx.Commit();
        }
    }

    public IReadOnlyList<string> KnownSensorIds()
    {
        lock (_writeLock)
        {
            var result = new List<string>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT sensor_id FROM sensor_meta ORDER BY sensor_id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }
            return result;
        }
    }

    public AiHistorySeriesResult? QuerySensorHistory(string sensorId, long fromUtcMs, long toUtcMs, int maxPoints)
    {
        lock (_writeLock)
        {
            var meta = ReadSensorMeta(sensorId);
            if (meta is null)
            {
                return null;
            }

            var minutes = (int)Math.Max(1, (toUtcMs - fromUtcMs) / 60_000L);
            var tier = AiHistoryRetention.PickTier(minutes);
            var points = tier switch
            {
                AiHistoryTier.Raw => QueryRawPoints(sensorId, fromUtcMs, toUtcMs),
                AiHistoryTier.OneMinute => QueryAggregatePoints("samples_1m", sensorId, fromUtcMs, toUtcMs),
                _ => QueryAggregatePoints("samples_5m", sensorId, fromUtcMs, toUtcMs),
            };

            return new AiHistorySeriesResult(sensorId, meta.Value.Name, meta.Value.Unit, tier, Thin(points, maxPoints));
        }
    }

    public IReadOnlyList<AiHistorySensorSummaryRow> Summarize(long fromUtcMs, long toUtcMs)
    {
        lock (_writeLock)
        {
            var minutes = (int)Math.Max(1, (toUtcMs - fromUtcMs) / 60_000L);
            var tier = AiHistoryRetention.PickTier(minutes);
            var stats = tier == AiHistoryTier.Raw
                ? SummarizeRaw(fromUtcMs, toUtcMs)
                : SummarizeAggregate(tier == AiHistoryTier.OneMinute ? "samples_1m" : "samples_5m", fromUtcMs, toUtcMs);

            var meta = ReadAllSensorMeta();
            var result = new List<AiHistorySensorSummaryRow>();
            foreach (var (sensorId, min, max, avg, samples) in stats)
            {
                if (!meta.TryGetValue(sensorId, out var m))
                {
                    continue;
                }
                result.Add(new AiHistorySensorSummaryRow(sensorId, m.Name, m.Unit, min, max, avg, m.LastValue, m.LastSeenUtcMs, samples));
            }
            return result;
        }
    }

    public void RecordEvent(AiHistoryEventRow row)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO events (ts_utc, kind, name, args_json, success, error_text)
                VALUES ($ts, $kind, $name, $args, $success, $error);
            """;
            cmd.Parameters.AddWithValue("$ts", row.TsUtcMs);
            cmd.Parameters.AddWithValue("$kind", row.Kind);
            cmd.Parameters.AddWithValue("$name", row.Name);
            cmd.Parameters.AddWithValue("$args", row.ArgsJson);
            cmd.Parameters.AddWithValue("$success", row.Success ? 1 : 0);
            cmd.Parameters.AddWithValue("$error", (object?)row.ErrorText ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public AiHistoryEventQueryResult QueryEvents(long fromUtcMs, string? type, int limit)
    {
        lock (_writeLock)
        {
            var rows = new List<AiHistoryEventRow>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT ts_utc, kind, name, args_json, success, error_text
                FROM events
                WHERE ts_utc >= $from AND ($type IS NULL OR kind = $type)
                ORDER BY ts_utc DESC
                LIMIT $limit;
            """;
            cmd.Parameters.AddWithValue("$from", fromUtcMs);
            cmd.Parameters.AddWithValue("$type", (object?)type ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$limit", limit + 1);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new AiHistoryEventRow(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt32(4) != 0, reader.IsDBNull(5) ? null : reader.GetString(5)));
            }

            var truncated = rows.Count > limit;
            if (truncated)
            {
                rows.RemoveAt(rows.Count - 1);
            }
            return new AiHistoryEventQueryResult(rows, truncated);
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

    // ── rollup / prune (called under _writeLock from RecordSamples) ─────────

    private void RollupIfDue(SqliteTransaction tx, long nowUtcMs)
    {
        var lastRollup1m = ReadRollupWatermark(tx, "rollup_1m");
        var bucketFloor1m = FloorTo(nowUtcMs, AiHistoryRetention.OneMinuteBucketMs);
        if (bucketFloor1m > lastRollup1m)
        {
            RollupRawToOneMinute(tx, lastRollup1m, bucketFloor1m);
            WriteRollupWatermark(tx, "rollup_1m", bucketFloor1m);
        }

        var lastRollup5m = ReadRollupWatermark(tx, "rollup_5m");
        var bucketFloor5m = FloorTo(nowUtcMs, AiHistoryRetention.FiveMinuteBucketMs);
        if (bucketFloor5m > lastRollup5m)
        {
            RollupOneMinuteToFiveMinute(tx, lastRollup5m, bucketFloor5m);
            WriteRollupWatermark(tx, "rollup_5m", bucketFloor5m);
        }
    }

    private static long FloorTo(long ms, long period) => ms / period * period;

    private void RollupRawToOneMinute(SqliteTransaction tx, long fromUtcMs, long toUtcMs)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO samples_1m (sensor_id, name, kind, unit, bucket_utc, min_value, max_value, sum_value, samples)
            SELECT sensor_id, MIN(name), MIN(kind), MIN(unit), (ts_utc / $bucket) * $bucket AS bucket,
                   MIN(value), MAX(value), SUM(value), COUNT(*)
            FROM samples_raw
            WHERE ts_utc >= $from AND ts_utc < $to
            GROUP BY sensor_id, bucket;
        """;
        cmd.Parameters.AddWithValue("$bucket", AiHistoryRetention.OneMinuteBucketMs);
        cmd.Parameters.AddWithValue("$from", fromUtcMs);
        cmd.Parameters.AddWithValue("$to", toUtcMs);
        cmd.ExecuteNonQuery();
    }

    private void RollupOneMinuteToFiveMinute(SqliteTransaction tx, long fromUtcMs, long toUtcMs)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO samples_5m (sensor_id, name, kind, unit, bucket_utc, min_value, max_value, sum_value, samples)
            SELECT sensor_id, MIN(name), MIN(kind), MIN(unit), (bucket_utc / $bucket) * $bucket AS bucket,
                   MIN(min_value), MAX(max_value), SUM(sum_value), SUM(samples)
            FROM samples_1m
            WHERE bucket_utc >= $from AND bucket_utc < $to
            GROUP BY sensor_id, bucket;
        """;
        cmd.Parameters.AddWithValue("$bucket", AiHistoryRetention.FiveMinuteBucketMs);
        cmd.Parameters.AddWithValue("$from", fromUtcMs);
        cmd.Parameters.AddWithValue("$to", toUtcMs);
        cmd.ExecuteNonQuery();
    }

    private void PruneOld(SqliteTransaction tx, long nowUtcMs)
    {
        DeleteOlderThan(tx, "samples_raw", "ts_utc", nowUtcMs - AiHistoryRetention.RawRetentionMinutes * 60_000L);
        DeleteOlderThan(tx, "samples_1m", "bucket_utc", nowUtcMs - AiHistoryRetention.OneMinuteTierRetentionMinutes * 60_000L);
        DeleteOlderThan(tx, "samples_5m", "bucket_utc", nowUtcMs - AiHistoryRetention.FiveMinuteTierRetentionMinutes * 60_000L);
    }

    private void DeleteOlderThan(SqliteTransaction tx, string table, string column, long cutoffMs)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {table} WHERE {column} < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", cutoffMs);
        cmd.ExecuteNonQuery();
    }

    private long ReadRollupWatermark(SqliteTransaction tx, string key)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value FROM schema_meta WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        var value = cmd.ExecuteScalar() as string;
        return value is not null && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0L;
    }

    private void WriteRollupWatermark(SqliteTransaction tx, string key, long value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO schema_meta (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value.ToString(CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    // ── query helpers ─────────────────────────────────────────────────────

    private (string Name, string Unit)? ReadSensorMeta(string sensorId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT name, unit FROM sensor_meta WHERE sensor_id = $sid;";
        cmd.Parameters.AddWithValue("$sid", sensorId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private Dictionary<string, (string Name, string Unit, double LastValue, long LastSeenUtcMs)> ReadAllSensorMeta()
    {
        var result = new Dictionary<string, (string, string, double, long)>(StringComparer.Ordinal);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT sensor_id, name, unit, last_value, last_seen_utc FROM sensor_meta;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2), reader.GetDouble(3), reader.GetInt64(4));
        }
        return result;
    }

    private List<AiHistoryPointRow> QueryRawPoints(string sensorId, long fromUtcMs, long toUtcMs)
    {
        var result = new List<AiHistoryPointRow>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT ts_utc, value FROM samples_raw
            WHERE sensor_id = $sid AND ts_utc >= $from AND ts_utc <= $to
            ORDER BY ts_utc ASC;
        """;
        cmd.Parameters.AddWithValue("$sid", sensorId);
        cmd.Parameters.AddWithValue("$from", fromUtcMs);
        cmd.Parameters.AddWithValue("$to", toUtcMs);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new AiHistoryPointRow(reader.GetInt64(0), reader.GetDouble(1)));
        }
        return result;
    }

    private List<AiHistoryPointRow> QueryAggregatePoints(string table, string sensorId, long fromUtcMs, long toUtcMs)
    {
        var result = new List<AiHistoryPointRow>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT bucket_utc, sum_value, samples FROM {table}
            WHERE sensor_id = $sid AND bucket_utc >= $from AND bucket_utc <= $to
            ORDER BY bucket_utc ASC;
        """;
        cmd.Parameters.AddWithValue("$sid", sensorId);
        cmd.Parameters.AddWithValue("$from", fromUtcMs);
        cmd.Parameters.AddWithValue("$to", toUtcMs);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var bucket = reader.GetInt64(0);
            var sum = reader.GetDouble(1);
            var samples = reader.GetInt32(2);
            result.Add(new AiHistoryPointRow(bucket, samples > 0 ? sum / samples : 0));
        }
        return result;
    }

    private static List<AiHistoryPointRow> Thin(List<AiHistoryPointRow> points, int maxPoints)
    {
        if (maxPoints <= 0 || points.Count <= maxPoints)
        {
            return points;
        }
        var stride = (int)Math.Ceiling(points.Count / (double)maxPoints);
        var result = new List<AiHistoryPointRow>();
        for (var i = 0; i < points.Count; i += stride)
        {
            result.Add(points[i]);
        }
        if ((result.Count - 1) * stride != points.Count - 1)
        {
            result.Add(points[^1]);
        }
        return result;
    }

    private List<(string SensorId, double Min, double Max, double Avg, int Samples)> SummarizeRaw(long fromUtcMs, long toUtcMs)
    {
        var result = new List<(string, double, double, double, int)>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT sensor_id, MIN(value), MAX(value), AVG(value), COUNT(*)
            FROM samples_raw
            WHERE ts_utc >= $from AND ts_utc <= $to
            GROUP BY sensor_id;
        """;
        cmd.Parameters.AddWithValue("$from", fromUtcMs);
        cmd.Parameters.AddWithValue("$to", toUtcMs);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetInt32(4)));
        }
        return result;
    }

    private List<(string SensorId, double Min, double Max, double Avg, int Samples)> SummarizeAggregate(string table, long fromUtcMs, long toUtcMs)
    {
        var result = new List<(string, double, double, double, int)>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT sensor_id, MIN(min_value), MAX(max_value), SUM(sum_value), SUM(samples)
            FROM {table}
            WHERE bucket_utc >= $from AND bucket_utc <= $to
            GROUP BY sensor_id;
        """;
        cmd.Parameters.AddWithValue("$from", fromUtcMs);
        cmd.Parameters.AddWithValue("$to", toUtcMs);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var samples = reader.GetInt32(4);
            var sum = reader.GetDouble(3);
            result.Add((reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2), samples > 0 ? sum / samples : 0, samples));
        }
        return result;
    }

    // ── setup ─────────────────────────────────────────────────────────────

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
            CREATE TABLE IF NOT EXISTS samples_raw (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                sensor_id TEXT    NOT NULL,
                name      TEXT    NOT NULL,
                kind      TEXT    NOT NULL,
                unit      TEXT    NOT NULL,
                ts_utc    INTEGER NOT NULL,
                value     REAL    NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_samples_raw_sensor_ts ON samples_raw(sensor_id, ts_utc);
            CREATE INDEX IF NOT EXISTS ix_samples_raw_ts ON samples_raw(ts_utc);

            CREATE TABLE IF NOT EXISTS samples_1m (
                sensor_id  TEXT    NOT NULL,
                name       TEXT    NOT NULL,
                kind       TEXT    NOT NULL,
                unit       TEXT    NOT NULL,
                bucket_utc INTEGER NOT NULL,
                min_value  REAL    NOT NULL,
                max_value  REAL    NOT NULL,
                sum_value  REAL    NOT NULL,
                samples    INTEGER NOT NULL,
                PRIMARY KEY (sensor_id, bucket_utc)
            );
            CREATE INDEX IF NOT EXISTS ix_samples_1m_bucket ON samples_1m(bucket_utc);

            CREATE TABLE IF NOT EXISTS samples_5m (
                sensor_id  TEXT    NOT NULL,
                name       TEXT    NOT NULL,
                kind       TEXT    NOT NULL,
                unit       TEXT    NOT NULL,
                bucket_utc INTEGER NOT NULL,
                min_value  REAL    NOT NULL,
                max_value  REAL    NOT NULL,
                sum_value  REAL    NOT NULL,
                samples    INTEGER NOT NULL,
                PRIMARY KEY (sensor_id, bucket_utc)
            );
            CREATE INDEX IF NOT EXISTS ix_samples_5m_bucket ON samples_5m(bucket_utc);

            CREATE TABLE IF NOT EXISTS sensor_meta (
                sensor_id     TEXT PRIMARY KEY,
                name          TEXT NOT NULL,
                kind          TEXT NOT NULL,
                unit          TEXT NOT NULL,
                last_value    REAL NOT NULL,
                last_seen_utc INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS events (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                ts_utc     INTEGER NOT NULL,
                kind       TEXT    NOT NULL,
                name       TEXT    NOT NULL,
                args_json  TEXT    NOT NULL,
                success    INTEGER NOT NULL,
                error_text TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_events_ts ON events(ts_utc);
            CREATE INDEX IF NOT EXISTS ix_events_kind ON events(kind);

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

    private static string ResolveDatabasePath() => Path.Combine(JsonConfigStore.ResolveDataDirectory(), "ai-history.db");
}
