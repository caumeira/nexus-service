using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Nexus.Service.Persistence;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// SQLite-backed persistent store for metrics.db. One long-lived connection,
/// one lock serializing reads and writes, WAL mode via SqliteStores (shared
/// with SqliteTemperatureHistoryStore / SqliteScreenTimeStore).
///
/// Schema: metric_seconds is a wide table (one row per second, scaled-int
/// columns - x10 fixed point halves storage versus REAL). gpu_seconds and
/// fan_seconds are narrow (ts, entity) pairs keyed by a small integer
/// surrogate resolved from gpu_series/fan_series, so the per-second rows
/// never repeat a string id.
/// </summary>
public sealed class SqliteMetricsHistoryStore : IMetricsHistoryStore
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly object _writeLock = new();

    private readonly Dictionary<string, long> _gpuKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _fanKeys = new(StringComparer.Ordinal);

    public SqliteMetricsHistoryStore() : this(ResolveDatabasePath()) { }

    public SqliteMetricsHistoryStore(string dbPath)
    {
        _dbPath = dbPath;
        _connection = SqliteStores.OpenConnection(dbPath);
        EnsureSchema();
        LoadKeyCaches();
    }

    public string DatabasePath => _dbPath;

    public void Append(IReadOnlyList<MetricSample> samples, long? pruneCutoffSec)
    {
        if (samples.Count == 0 && pruneCutoffSec is null)
        {
            return;
        }

        lock (_writeLock)
        {
            using var tx = _connection.BeginTransaction();
            var pendingGpuKeys = new Dictionary<string, long>(StringComparer.Ordinal);
            var pendingFanKeys = new Dictionary<string, long>(StringComparer.Ordinal);

            if (samples.Count > 0)
            {
                InsertScalars(tx, samples);
                InsertGpuReadings(tx, samples, pendingGpuKeys);
                InsertFanReadings(tx, samples, pendingFanKeys);
            }

            if (pruneCutoffSec is { } cutoff)
            {
                Prune(tx, cutoff);
            }

            tx.Commit();

            // Only fold newly resolved surrogate keys into the permanent
            // cache once the transaction that inserted their gpu_series/
            // fan_series row has actually committed - caching them earlier
            // would survive a rollback and point at a series row that was
            // never persisted, so every later query for that entity would
            // silently return nothing (foreign_keys=OFF, so the dangling
            // reference never errors).
            foreach (var (id, key) in pendingGpuKeys)
            {
                _gpuKeys[id] = key;
            }
            foreach (var (id, key) in pendingFanKeys)
            {
                _fanKeys[id] = key;
            }
        }
    }

    public IReadOnlyList<MetricSample> Query(long fromSec, long toSec)
    {
        lock (_writeLock)
        {
            var scalars = new SortedDictionary<long, ScalarRow>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT ts, cpu_x10, mem_x10, net_in_bps, net_out_bps, cpu_temp_x10
                    FROM metric_seconds
                    WHERE ts BETWEEN $from AND $to
                    ORDER BY ts ASC;
                """;
                cmd.Parameters.AddWithValue("$from", fromSec);
                cmd.Parameters.AddWithValue("$to", toSec);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var ts = reader.GetInt64(0);
                    scalars[ts] = new ScalarRow(
                        UnscaleX10(reader, 1),
                        UnscaleX10(reader, 2),
                        reader.IsDBNull(3) ? null : reader.GetInt64(3),
                        reader.IsDBNull(4) ? null : reader.GetInt64(4),
                        UnscaleX10(reader, 5));
                }
            }

            var gpuByTs = new Dictionary<long, List<GpuReading>>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT gs.ts, se.gpu_id, se.name, gs.load_x10, gs.temp_x10
                    FROM gpu_seconds gs
                    JOIN gpu_series se ON se.key = gs.gpu
                    WHERE gs.ts BETWEEN $from AND $to
                    ORDER BY gs.ts ASC;
                """;
                cmd.Parameters.AddWithValue("$from", fromSec);
                cmd.Parameters.AddWithValue("$to", toSec);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var ts = reader.GetInt64(0);
                    var reading = new GpuReading(
                        reader.GetString(1), reader.GetString(2), "",
                        UnscaleX10(reader, 3), UnscaleX10(reader, 4));
                    AddTo(gpuByTs, ts, reading);
                }
            }

            var fanByTs = new Dictionary<long, List<FanReading>>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT fs.ts, se.fan_id, se.name, fs.rpm, fs.duty
                    FROM fan_seconds fs
                    JOIN fan_series se ON se.key = fs.fan
                    WHERE fs.ts BETWEEN $from AND $to
                    ORDER BY fs.ts ASC;
                """;
                cmd.Parameters.AddWithValue("$from", fromSec);
                cmd.Parameters.AddWithValue("$to", toSec);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var ts = reader.GetInt64(0);
                    var reading = new FanReading(
                        reader.GetString(1), reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetInt32(4));
                    AddTo(fanByTs, ts, reading);
                }
            }

            var result = new List<MetricSample>(scalars.Count);
            foreach (var (ts, row) in scalars)
            {
                result.Add(new MetricSample(
                    ts, row.Cpu, row.Mem, row.NetIn, row.NetOut, row.CpuTemp,
                    gpuByTs.TryGetValue(ts, out var gpus) ? gpus : Array.Empty<GpuReading>(),
                    fanByTs.TryGetValue(ts, out var fans) ? fans : Array.Empty<FanReading>()));
            }
            return result;
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

    private readonly record struct ScalarRow(
        double? Cpu, double? Mem, long? NetIn, long? NetOut, double? CpuTemp);

    private static void AddTo<T>(Dictionary<long, List<T>> map, long ts, T value)
    {
        if (!map.TryGetValue(ts, out var list))
        {
            list = new List<T>();
            map[ts] = list;
        }
        list.Add(value);
    }

    private void InsertScalars(SqliteTransaction tx, IReadOnlyList<MetricSample> samples)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO metric_seconds (ts, cpu_x10, mem_x10, net_in_bps, net_out_bps, cpu_temp_x10)
            VALUES ($ts, $cpu, $mem, $netIn, $netOut, $cpuTemp);
        """;
        var pTs = AddParam(cmd, "$ts");
        var pCpu = AddParam(cmd, "$cpu");
        var pMem = AddParam(cmd, "$mem");
        var pNetIn = AddParam(cmd, "$netIn");
        var pNetOut = AddParam(cmd, "$netOut");
        var pCpuTemp = AddParam(cmd, "$cpuTemp");

        foreach (var s in samples)
        {
            pTs.Value = s.TsSec;
            pCpu.Value = ScaleX10(s.CpuPercent);
            pMem.Value = ScaleX10(s.MemoryPercent);
            pNetIn.Value = ScaleWhole(s.NetInBytesPerSec);
            pNetOut.Value = ScaleWhole(s.NetOutBytesPerSec);
            pCpuTemp.Value = ScaleX10(s.CpuTempC);
            cmd.ExecuteNonQuery();
        }
    }

    private void InsertGpuReadings(
        SqliteTransaction tx, IReadOnlyList<MetricSample> samples, Dictionary<string, long> pendingKeys)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO gpu_seconds (ts, gpu, load_x10, temp_x10)
            VALUES ($ts, $gpu, $load, $temp);
        """;
        var pTs = AddParam(cmd, "$ts");
        var pGpu = AddParam(cmd, "$gpu");
        var pLoad = AddParam(cmd, "$load");
        var pTemp = AddParam(cmd, "$temp");

        foreach (var s in samples)
        {
            foreach (var g in s.Gpus)
            {
                pTs.Value = s.TsSec;
                pGpu.Value = ResolveGpuKey(tx, g.GpuId, g.Name, pendingKeys);
                pLoad.Value = ScaleX10(g.LoadPercent);
                pTemp.Value = ScaleX10(g.TempC);
                cmd.ExecuteNonQuery();
            }
        }
    }

    private void InsertFanReadings(
        SqliteTransaction tx, IReadOnlyList<MetricSample> samples, Dictionary<string, long> pendingKeys)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO fan_seconds (ts, fan, rpm, duty)
            VALUES ($ts, $fan, $rpm, $duty);
        """;
        var pTs = AddParam(cmd, "$ts");
        var pFan = AddParam(cmd, "$fan");
        var pRpm = AddParam(cmd, "$rpm");
        var pDuty = AddParam(cmd, "$duty");

        foreach (var s in samples)
        {
            foreach (var f in s.Fans)
            {
                pTs.Value = s.TsSec;
                pFan.Value = ResolveFanKey(tx, f.FanId, f.Name, pendingKeys);
                pRpm.Value = (object?)f.Rpm ?? DBNull.Value;
                pDuty.Value = (object?)f.Duty ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
        }
    }

    private void Prune(SqliteTransaction tx, long cutoffSec)
    {
        foreach (var table in new[] { "metric_seconds", "gpu_seconds", "fan_seconds" })
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE ts < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoffSec);
            cmd.ExecuteNonQuery();
        }
    }

    private long ResolveGpuKey(SqliteTransaction tx, string gpuId, string name, Dictionary<string, long> pendingKeys)
    {
        if (_gpuKeys.TryGetValue(gpuId, out var cached))
        {
            return cached;
        }
        if (pendingKeys.TryGetValue(gpuId, out var pending))
        {
            return pending;
        }

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO gpu_series (gpu_id, name) VALUES ($id, $name)
            ON CONFLICT(gpu_id) DO UPDATE SET name = excluded.name
            RETURNING key;
        """;
        cmd.Parameters.AddWithValue("$id", gpuId);
        cmd.Parameters.AddWithValue("$name", name);
        var key = Convert.ToInt64(cmd.ExecuteScalar());
        pendingKeys[gpuId] = key;
        return key;
    }

    private long ResolveFanKey(SqliteTransaction tx, string fanId, string name, Dictionary<string, long> pendingKeys)
    {
        if (_fanKeys.TryGetValue(fanId, out var cached))
        {
            return cached;
        }
        if (pendingKeys.TryGetValue(fanId, out var pending))
        {
            return pending;
        }

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO fan_series (fan_id, name) VALUES ($id, $name)
            ON CONFLICT(fan_id) DO UPDATE SET name = excluded.name
            RETURNING key;
        """;
        cmd.Parameters.AddWithValue("$id", fanId);
        cmd.Parameters.AddWithValue("$name", name);
        var key = Convert.ToInt64(cmd.ExecuteScalar());
        pendingKeys[fanId] = key;
        return key;
    }

    private void LoadKeyCaches()
    {
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT gpu_id, key FROM gpu_series;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                _gpuKeys[reader.GetString(0)] = reader.GetInt64(1);
            }
        }
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT fan_id, key FROM fan_series;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                _fanKeys[reader.GetString(0)] = reader.GetInt64(1);
            }
        }
    }

    private static object ScaleX10(double? value) =>
        value is { } v ? (object)(int)Math.Round(v * 10) : DBNull.Value;

    private static object ScaleWhole(double? value) =>
        value is { } v ? (object)(long)Math.Round(v) : DBNull.Value;

    private static double? UnscaleX10(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal) / 10.0;

    private static SqliteParameter AddParam(SqliteCommand cmd, string name)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS metric_seconds (
                ts           INTEGER PRIMARY KEY,
                cpu_x10      INTEGER,
                mem_x10      INTEGER,
                net_in_bps   INTEGER,
                net_out_bps  INTEGER,
                cpu_temp_x10 INTEGER
            );

            CREATE TABLE IF NOT EXISTS gpu_series (
                key    INTEGER PRIMARY KEY AUTOINCREMENT,
                gpu_id TEXT NOT NULL UNIQUE,
                name   TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS gpu_seconds (
                ts       INTEGER NOT NULL,
                gpu      INTEGER NOT NULL,
                load_x10 INTEGER,
                temp_x10 INTEGER,
                PRIMARY KEY (ts, gpu)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS fan_series (
                key    INTEGER PRIMARY KEY AUTOINCREMENT,
                fan_id TEXT NOT NULL UNIQUE,
                name   TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS fan_seconds (
                ts   INTEGER NOT NULL,
                fan  INTEGER NOT NULL,
                rpm  INTEGER,
                duty INTEGER,
                PRIMARY KEY (ts, fan)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS schema_meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT OR IGNORE INTO schema_meta (key, value) VALUES ('version', '1');
        """;
        cmd.ExecuteNonQuery();
    }

    private static string ResolveDatabasePath() =>
        Path.Combine(NexusDataPaths.DatabaseDir(), "metrics.db");
}
