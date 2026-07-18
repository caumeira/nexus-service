using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Nexus.Service.Persistence;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// SQLite-backed persistent store for metrics.db. One long-lived connection,
/// one lock serializing reads and writes, WAL mode via SqliteStores.
///
/// Schema: metric_seconds is a wide table (one row per second, scaled-int
/// columns - x10 fixed point halves storage versus REAL). gpu_seconds,
/// fan_seconds, and temp_component_seconds are narrow (ts, entity) pairs
/// keyed by a small integer surrogate resolved from gpu_series/fan_series/
/// temp_component_series, so the per-second rows never repeat a string id.
/// privacy_sessions is unrelated to 1Hz sampling (PrivacyAccessWatcher
/// writes it, on its own transition-driven cadence) but shares this
/// connection and lock since its write rate is low.
///
/// temp_component_series/temp_component_seconds hold storage and RAM
/// temperature only - CPU temperature already lives in metric_seconds and
/// GPU temperature in gpu_seconds, both sampled well before this store took
/// on temperature history. temp_buckets is the single long-retention (see
/// MetricsHistory.TempRetentionDays) rollup unifying all four temperature
/// kinds by a plain TEXT component id, rather than a fourth surrogate-key
/// table: it has no non-temperature columns to keep narrow, and a flat id is
/// what GET /diagnostics/temperatures and TemperatureInsights already
/// consume (see TemperatureBucketRow) with no join needed to read it back.
/// Its bucket width (MetricsHistory.TempBucketMinutes) is wider than
/// metric_minutes/gpu_minutes/fan_minutes' 1-minute rollup on purpose: at
/// TempRetentionDays' window, a 1-minute width would scan proportionally
/// more rows for the same query with no chart benefit - TierWidthMinutesFor
/// never asks for a tier narrower than the native bucket width.
///
/// app_cpu_seconds / app_mem_seconds / app_gpu_seconds / app_vram_seconds are
/// per-app usage history, sampled on MetricsHistory.AppSampleIntervalSeconds's
/// sub-cadence: four narrow tables, one per metric, instead of a single (ts,
/// app, metric) table - avoids a repeated TEXT metric discriminator per row
/// at the row counts this table reaches (MetricsHistory.TopAppsPerSample
/// apps per metric per tick, retained for MetricsHistory.RetentionDays), and
/// lets each metric's read query hit a purpose-built PK/index with no filter
/// predicate on metric. app_gpu_seconds/app_vram_seconds reuse gpu_series'
/// surrogate key rather than minting a duplicate GPU id mapping;
/// app_vram_seconds is ranked independently by VRAM rather than piggybacking
/// on app_gpu_seconds' own vram_mb column, since a process can hold
/// significant VRAM while nearly idle on that adapter.
/// </summary>
public sealed class SqliteMetricsHistoryStore : IMetricsHistoryStore, IPrivacySessionStore, IAppUsageHistoryStore
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly object _writeLock = new();

    private readonly Dictionary<string, long> _gpuKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _fanKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _tempComponentKeys = new(StringComparer.Ordinal);
    // OrdinalIgnoreCase matches app_series.name's COLLATE NOCASE and
    // ProcessAggregation/ResolveExecutablePath's name comparisons - a
    // process name observed with differing case must resolve to the same
    // app row, not fragment into a second one.
    private readonly Dictionary<string, long> _appKeys = new(StringComparer.OrdinalIgnoreCase);

    // Oldest ts a temp_buckets rebuild may trust metric_seconds/gpu_seconds/
    // temp_component_seconds to still hold in full - see UpsertTempBucketsRollup.
    // Seeded from the raw tables' own minimum at open (state a prior session
    // already pruned to) and only ever raised by Prune, never re-derived from
    // live table contents mid-session: a stray old-timestamped sample sits in
    // metric_seconds the moment InsertScalars writes it, so querying MIN(ts)
    // after that point would let the very row this guards against widen the
    // floor back to itself.
    private long? _sourceFloorSec;

    public SqliteMetricsHistoryStore() : this(ResolveDatabasePath()) { }

    public SqliteMetricsHistoryStore(string dbPath)
    {
        _dbPath = dbPath;
        _connection = SqliteStores.OpenConnection(dbPath);
        EnsureSchema();
        LoadKeyCaches();
        _sourceFloorSec = QueryMinRawSecond();
    }

    private long? QueryMinRawSecond()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT MIN(ts) FROM metric_seconds;";
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result);
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
            var pendingTempComponentKeys = new Dictionary<string, long>(StringComparer.Ordinal);

            if (samples.Count > 0)
            {
                InsertScalars(tx, samples);
                InsertGpuReadings(tx, samples, pendingGpuKeys);
                InsertFanReadings(tx, samples, pendingFanKeys);
                InsertComponentTempReadings(tx, samples, pendingTempComponentKeys);
                UpsertScalarRollup(tx, samples);
                UpsertGpuRollup(tx, samples, pendingGpuKeys);
                UpsertFanRollup(tx, samples, pendingFanKeys);
                UpsertTempBucketsRollup(tx, samples, pendingGpuKeys, pendingTempComponentKeys);
            }

            if (pruneCutoffSec is { } cutoff)
            {
                Prune(tx, cutoff);
            }

            tx.Commit();

            // Only fold newly resolved surrogate keys into the permanent
            // cache once the transaction that inserted their gpu_series/
            // fan_series/temp_component_series row has actually committed -
            // caching them earlier would survive a rollback and point at a
            // series row that was never persisted, so every later query for
            // that entity would silently return nothing (foreign_keys=OFF,
            // so the dangling reference never errors).
            foreach (var (id, key) in pendingGpuKeys)
            {
                _gpuKeys[id] = key;
            }
            foreach (var (id, key) in pendingFanKeys)
            {
                _fanKeys[id] = key;
            }
            foreach (var (id, key) in pendingTempComponentKeys)
            {
                _tempComponentKeys[id] = key;
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
                    SELECT ts, cpu_x10, mem_x10, net_in_bps, net_out_bps, cpu_temp_x10, disk_read_bps, disk_write_bps
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
                        UnscaleX10(reader, 5),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6),
                        reader.IsDBNull(7) ? null : reader.GetInt64(7));
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

            var componentByTs = new Dictionary<long, List<ComponentTempReading>>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT cs.ts, se.component_id, se.kind, se.name, cs.value_x10
                    FROM temp_component_seconds cs
                    JOIN temp_component_series se ON se.key = cs.component
                    WHERE cs.ts BETWEEN $from AND $to
                    ORDER BY cs.ts ASC;
                """;
                cmd.Parameters.AddWithValue("$from", fromSec);
                cmd.Parameters.AddWithValue("$to", toSec);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var ts = reader.GetInt64(0);
                    var reading = new ComponentTempReading(
                        reader.GetString(1), reader.GetString(2), reader.GetString(3), UnscaleX10(reader, 4));
                    AddTo(componentByTs, ts, reading);
                }
            }

            var result = new List<MetricSample>(scalars.Count);
            foreach (var (ts, row) in scalars)
            {
                result.Add(new MetricSample(
                    ts, row.Cpu, row.Mem, row.NetIn, row.NetOut, row.CpuTemp,
                    gpuByTs.TryGetValue(ts, out var gpus) ? gpus : Array.Empty<GpuReading>(),
                    fanByTs.TryGetValue(ts, out var fans) ? fans : Array.Empty<FanReading>(),
                    ComponentTemps: componentByTs.TryGetValue(ts, out var comps) ? comps : Array.Empty<ComponentTempReading>(),
                    DiskReadBytesPerSec: row.DiskRead, DiskWriteBytesPerSec: row.DiskWrite));
            }
            return result;
        }
    }

    // The rollup is minute-aligned, so it can only serve a step that is a
    // whole multiple of a minute - every MetricsHistory.StepLadderSeconds
    // rung at or above that width already is, so MonitoringHistoryRoutes'
    // DecimatedPathMinStepSeconds means the *FromRaw variants below are
    // unreached from the real ladder and stay only as a defensive fallback
    // for a non-ladder step, exercised directly by ScalarDecimatedRawSpec
    // rather than through the route.
    private static bool IsRollupEligible(int stepSeconds) => stepSeconds >= 60 && stepSeconds % 60 == 0;

    public IReadOnlyList<ScalarDecimatedSlot> QueryScalarsDecimated(long fromSec, long toSec, int stepSeconds)
    {
        lock (_writeLock)
        {
            return IsRollupEligible(stepSeconds)
                ? QueryScalarsDecimatedFromRollup(fromSec, toSec, stepSeconds)
                : QueryScalarsDecimatedFromRaw(fromSec, toSec, stepSeconds);
        }
    }

    private IReadOnlyList<ScalarDecimatedSlot> QueryScalarsDecimatedFromRaw(long fromSec, long toSec, int stepSeconds)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT (ts / $step) * $step AS slot,
                   AVG(cpu_x10) / 10.0, MAX(cpu_x10) / 10.0,
                   AVG(mem_x10) / 10.0, MAX(mem_x10) / 10.0,
                   AVG(net_in_bps), MAX(net_in_bps),
                   AVG(net_out_bps), MAX(net_out_bps),
                   AVG(cpu_temp_x10) / 10.0, MAX(cpu_temp_x10) / 10.0,
                   AVG(disk_read_bps), MAX(disk_read_bps),
                   AVG(disk_write_bps), MAX(disk_write_bps)
            FROM metric_seconds
            WHERE ts BETWEEN $from AND $to
            GROUP BY slot
            ORDER BY slot;
        """;
        cmd.Parameters.AddWithValue("$step", stepSeconds);
        cmd.Parameters.AddWithValue("$from", fromSec);
        cmd.Parameters.AddWithValue("$to", toSec);

        var result = new List<ScalarDecimatedSlot>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ScalarDecimatedSlot(
                reader.GetInt64(0),
                NullableDouble(reader, 1), NullableDouble(reader, 2),
                NullableDouble(reader, 3), NullableDouble(reader, 4),
                NullableDouble(reader, 5), NullableDouble(reader, 6),
                NullableDouble(reader, 7), NullableDouble(reader, 8),
                NullableDouble(reader, 9), NullableDouble(reader, 10),
                NullableDouble(reader, 11), NullableDouble(reader, 12),
                NullableDouble(reader, 13), NullableDouble(reader, 14)));
        }
        return result;
    }

    private IReadOnlyList<ScalarDecimatedSlot> QueryScalarsDecimatedFromRollup(long fromSec, long toSec, int stepSeconds)
    {
        // ts_min is a minute floor, but from/to are arbitrary caller
        // seconds, so a tight ts_min BETWEEN from AND to drops a whole
        // minute whenever its floor lands before from even though part of
        // that minute is inside the window - the filter below is an
        // overlap test (does [ts_min, ts_min+59] intersect [from, to]),
        // matching a minute-granularity source's actual coverage.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT (ts_min / $step) * $step AS slot,
                   SUM(cpu_sum_x10) * 1.0 / SUM(cpu_cnt) / 10.0, MAX(cpu_max_x10) / 10.0,
                   SUM(mem_sum_x10) * 1.0 / SUM(mem_cnt) / 10.0, MAX(mem_max_x10) / 10.0,
                   SUM(net_in_sum) * 1.0 / SUM(net_in_cnt), MAX(net_in_max),
                   SUM(net_out_sum) * 1.0 / SUM(net_out_cnt), MAX(net_out_max),
                   SUM(cpu_temp_sum_x10) * 1.0 / SUM(cpu_temp_cnt) / 10.0, MAX(cpu_temp_max_x10) / 10.0,
                   SUM(disk_read_sum) * 1.0 / SUM(disk_read_cnt), MAX(disk_read_max),
                   SUM(disk_write_sum) * 1.0 / SUM(disk_write_cnt), MAX(disk_write_max)
            FROM metric_minutes
            WHERE ts_min + 59 >= $from AND ts_min <= $to
            GROUP BY slot
            ORDER BY slot;
        """;
        cmd.Parameters.AddWithValue("$step", stepSeconds);
        cmd.Parameters.AddWithValue("$from", fromSec);
        cmd.Parameters.AddWithValue("$to", toSec);

        var result = new List<ScalarDecimatedSlot>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ScalarDecimatedSlot(
                reader.GetInt64(0),
                NullableDouble(reader, 1), NullableDouble(reader, 2),
                NullableDouble(reader, 3), NullableDouble(reader, 4),
                NullableDouble(reader, 5), NullableDouble(reader, 6),
                NullableDouble(reader, 7), NullableDouble(reader, 8),
                NullableDouble(reader, 9), NullableDouble(reader, 10),
                NullableDouble(reader, 11), NullableDouble(reader, 12),
                NullableDouble(reader, 13), NullableDouble(reader, 14)));
        }
        return result;
    }

    public IReadOnlyList<GpuDecimatedSlot> QueryGpuDecimated(long fromSec, long toSec, int stepSeconds)
    {
        lock (_writeLock)
        {
            return IsRollupEligible(stepSeconds)
                ? QueryGpuDecimatedFromRollup(fromSec, toSec, stepSeconds)
                : QueryGpuDecimatedFromRaw(fromSec, toSec, stepSeconds);
        }
    }

    private IReadOnlyList<GpuDecimatedSlot> QueryGpuDecimatedFromRaw(long fromSec, long toSec, int stepSeconds)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT (gs.ts / $step) * $step AS slot, se.gpu_id, se.name,
                   AVG(gs.load_x10) / 10.0, MAX(gs.load_x10) / 10.0,
                   AVG(gs.temp_x10) / 10.0, MAX(gs.temp_x10) / 10.0
            FROM gpu_seconds gs
            JOIN gpu_series se ON se.key = gs.gpu
            WHERE gs.ts BETWEEN $from AND $to
            GROUP BY slot, gs.gpu
            ORDER BY se.gpu_id, slot;
        """;
        cmd.Parameters.AddWithValue("$step", stepSeconds);
        cmd.Parameters.AddWithValue("$from", fromSec);
        cmd.Parameters.AddWithValue("$to", toSec);

        var result = new List<GpuDecimatedSlot>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new GpuDecimatedSlot(
                reader.GetString(1), reader.GetString(2), reader.GetInt64(0),
                NullableDouble(reader, 3), NullableDouble(reader, 4),
                NullableDouble(reader, 5), NullableDouble(reader, 6)));
        }
        return result;
    }

    private IReadOnlyList<GpuDecimatedSlot> QueryGpuDecimatedFromRollup(long fromSec, long toSec, int stepSeconds)
    {
        // See QueryScalarsDecimatedFromRollup: an overlap test, not a tight
        // BETWEEN, since ts_min is a minute floor.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT (gm.ts_min / $step) * $step AS slot, se.gpu_id, se.name,
                   SUM(gm.load_sum_x10) * 1.0 / SUM(gm.load_cnt) / 10.0, MAX(gm.load_max_x10) / 10.0,
                   SUM(gm.temp_sum_x10) * 1.0 / SUM(gm.temp_cnt) / 10.0, MAX(gm.temp_max_x10) / 10.0
            FROM gpu_minutes gm
            JOIN gpu_series se ON se.key = gm.gpu
            WHERE gm.ts_min + 59 >= $from AND gm.ts_min <= $to
            GROUP BY slot, gm.gpu
            ORDER BY se.gpu_id, slot;
        """;
        cmd.Parameters.AddWithValue("$step", stepSeconds);
        cmd.Parameters.AddWithValue("$from", fromSec);
        cmd.Parameters.AddWithValue("$to", toSec);

        var result = new List<GpuDecimatedSlot>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new GpuDecimatedSlot(
                reader.GetString(1), reader.GetString(2), reader.GetInt64(0),
                NullableDouble(reader, 3), NullableDouble(reader, 4),
                NullableDouble(reader, 5), NullableDouble(reader, 6)));
        }
        return result;
    }

    public IReadOnlyList<FanDecimatedSlot> QueryFanDecimated(long fromSec, long toSec, int stepSeconds)
    {
        lock (_writeLock)
        {
            return IsRollupEligible(stepSeconds)
                ? QueryFanDecimatedFromRollup(fromSec, toSec, stepSeconds)
                : QueryFanDecimatedFromRaw(fromSec, toSec, stepSeconds);
        }
    }

    private IReadOnlyList<FanDecimatedSlot> QueryFanDecimatedFromRaw(long fromSec, long toSec, int stepSeconds)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT (fs.ts / $step) * $step AS slot, se.fan_id, se.name,
                   AVG(fs.rpm), MAX(fs.rpm),
                   AVG(fs.duty), MAX(fs.duty)
            FROM fan_seconds fs
            JOIN fan_series se ON se.key = fs.fan
            WHERE fs.ts BETWEEN $from AND $to
            GROUP BY slot, fs.fan
            ORDER BY se.fan_id, slot;
        """;
        cmd.Parameters.AddWithValue("$step", stepSeconds);
        cmd.Parameters.AddWithValue("$from", fromSec);
        cmd.Parameters.AddWithValue("$to", toSec);

        var result = new List<FanDecimatedSlot>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new FanDecimatedSlot(
                reader.GetString(1), reader.GetString(2), reader.GetInt64(0),
                NullableDouble(reader, 3), NullableDouble(reader, 4),
                NullableDouble(reader, 5), NullableDouble(reader, 6)));
        }
        return result;
    }

    private IReadOnlyList<FanDecimatedSlot> QueryFanDecimatedFromRollup(long fromSec, long toSec, int stepSeconds)
    {
        // See QueryScalarsDecimatedFromRollup: an overlap test, not a tight
        // BETWEEN, since ts_min is a minute floor.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT (fm.ts_min / $step) * $step AS slot, se.fan_id, se.name,
                   SUM(fm.rpm_sum) * 1.0 / SUM(fm.rpm_cnt), MAX(fm.rpm_max),
                   SUM(fm.duty_sum) * 1.0 / SUM(fm.duty_cnt), MAX(fm.duty_max)
            FROM fan_minutes fm
            JOIN fan_series se ON se.key = fm.fan
            WHERE fm.ts_min + 59 >= $from AND fm.ts_min <= $to
            GROUP BY slot, fm.fan
            ORDER BY se.fan_id, slot;
        """;
        cmd.Parameters.AddWithValue("$step", stepSeconds);
        cmd.Parameters.AddWithValue("$from", fromSec);
        cmd.Parameters.AddWithValue("$to", toSec);

        var result = new List<FanDecimatedSlot>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new FanDecimatedSlot(
                reader.GetString(1), reader.GetString(2), reader.GetInt64(0),
                NullableDouble(reader, 3), NullableDouble(reader, 4),
                NullableDouble(reader, 5), NullableDouble(reader, 6)));
        }
        return result;
    }

    // No temp_component_minutes rollup exists (see the class doc), so this
    // always aggregates the raw table directly, unlike QueryGpuDecimated/
    // QueryFanDecimated's rollup-eligible fast path.
    public IReadOnlyList<ComponentTempDecimatedSlot> QueryComponentTempDecimated(long fromSec, long toSec, int stepSeconds)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT (cs.ts / $step) * $step AS slot, se.component_id, se.kind, se.name,
                       AVG(cs.value_x10) / 10.0, MAX(cs.value_x10) / 10.0
                FROM temp_component_seconds cs
                JOIN temp_component_series se ON se.key = cs.component
                WHERE cs.ts BETWEEN $from AND $to
                GROUP BY slot, cs.component
                ORDER BY se.component_id, slot;
            """;
            cmd.Parameters.AddWithValue("$step", stepSeconds);
            cmd.Parameters.AddWithValue("$from", fromSec);
            cmd.Parameters.AddWithValue("$to", toSec);

            var result = new List<ComponentTempDecimatedSlot>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ComponentTempDecimatedSlot(
                    reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(0),
                    NullableDouble(reader, 4), NullableDouble(reader, 5)));
            }
            return result;
        }
    }

    // fromUtcMs/toUtcMs are milliseconds (the wire convention the temperature
    // route already used); temp_buckets.bucket_ts is seconds, matching every
    // other table here, so both bounds are floor-divided rather than rounded -
    // a window boundary landing mid-second still includes that second's bucket.
    public IReadOnlyList<TemperatureBucketRow> QueryTemperatureBuckets(long fromUtcMs, long toUtcMs)
    {
        var fromSec = fromUtcMs / 1000;
        var toSec = toUtcMs / 1000;

        lock (_writeLock)
        {
            var result = new List<TemperatureBucketRow>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT bucket_ts, component_id, kind, name, sum_x10, cnt, max_x10
                FROM temp_buckets
                WHERE bucket_ts BETWEEN $from AND $to
                ORDER BY bucket_ts ASC;
            """;
            cmd.Parameters.AddWithValue("$from", fromSec);
            cmd.Parameters.AddWithValue("$to", toSec);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var cnt = reader.GetInt32(5);
                var sumX10 = reader.GetInt64(4);
                var maxX10 = reader.GetInt64(6);
                result.Add(new TemperatureBucketRow(
                    reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt64(0) * 1000, sumX10 / (cnt * 10.0), maxX10 / 10.0, cnt));
            }
            return result;
        }
    }

    private static double? NullableDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

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
        double? Cpu, double? Mem, long? NetIn, long? NetOut, double? CpuTemp, long? DiskRead, long? DiskWrite);

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
            INSERT OR REPLACE INTO metric_seconds (ts, cpu_x10, mem_x10, net_in_bps, net_out_bps, cpu_temp_x10, disk_read_bps, disk_write_bps)
            VALUES ($ts, $cpu, $mem, $netIn, $netOut, $cpuTemp, $diskRead, $diskWrite);
        """;
        var pTs = AddParam(cmd, "$ts");
        var pCpu = AddParam(cmd, "$cpu");
        var pMem = AddParam(cmd, "$mem");
        var pNetIn = AddParam(cmd, "$netIn");
        var pNetOut = AddParam(cmd, "$netOut");
        var pCpuTemp = AddParam(cmd, "$cpuTemp");
        var pDiskRead = AddParam(cmd, "$diskRead");
        var pDiskWrite = AddParam(cmd, "$diskWrite");

        foreach (var s in samples)
        {
            pTs.Value = s.TsSec;
            pCpu.Value = ScaleX10(s.CpuPercent);
            pMem.Value = ScaleX10(s.MemoryPercent);
            pNetIn.Value = ScaleWhole(s.NetInBytesPerSec);
            pNetOut.Value = ScaleWhole(s.NetOutBytesPerSec);
            pCpuTemp.Value = ScaleX10(s.CpuTempC);
            pDiskRead.Value = ScaleWhole(s.DiskReadBytesPerSec);
            pDiskWrite.Value = ScaleWhole(s.DiskWriteBytesPerSec);
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

    private void InsertComponentTempReadings(
        SqliteTransaction tx, IReadOnlyList<MetricSample> samples, Dictionary<string, long> pendingKeys)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO temp_component_seconds (ts, component, value_x10)
            VALUES ($ts, $component, $value);
        """;
        var pTs = AddParam(cmd, "$ts");
        var pComponent = AddParam(cmd, "$component");
        var pValue = AddParam(cmd, "$value");

        foreach (var s in samples)
        {
            foreach (var c in s.ComponentTemps)
            {
                pTs.Value = s.TsSec;
                pComponent.Value = ResolveTempComponentKey(tx, c.ComponentId, c.Kind, c.Name, pendingKeys);
                pValue.Value = ScaleX10(c.ValueC);
                cmd.ExecuteNonQuery();
            }
        }
    }

    // Rebuilds (never accumulates) each touched minute's row from an
    // aggregate query over the raw per-second table, run after
    // InsertScalars/InsertGpuReadings/InsertFanReadings inside the same
    // transaction so the SELECT sees this flush's just-written rows. A
    // rebuild is idempotent no matter how many times a given ts lands here
    // (a backward clock step can replay one): the raw table dedupes it via
    // INSERT OR REPLACE, and the rebuilt row is a fresh aggregate over
    // whatever rows exist now, not an addition on top of what was already
    // stored. SUM/MAX/COUNT over a column all ignore NULL rows, so cnt
    // stays the count of actual readings and sum/max stay unaffected by a
    // ts with no value for that field.
    private static long ResolveKnownKey(string id, Dictionary<string, long> cache, Dictionary<string, long> pendingKeys) =>
        cache.TryGetValue(id, out var key) ? key : pendingKeys[id];

    private void UpsertScalarRollup(SqliteTransaction tx, IReadOnlyList<MetricSample> samples)
    {
        var minutes = new HashSet<long>();
        foreach (var s in samples)
        {
            minutes.Add(s.TsSec / 60 * 60);
        }

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO metric_minutes (
                ts_min, cpu_sum_x10, cpu_cnt, cpu_max_x10,
                mem_sum_x10, mem_cnt, mem_max_x10,
                net_in_sum, net_in_cnt, net_in_max,
                net_out_sum, net_out_cnt, net_out_max,
                cpu_temp_sum_x10, cpu_temp_cnt, cpu_temp_max_x10,
                disk_read_sum, disk_read_cnt, disk_read_max,
                disk_write_sum, disk_write_cnt, disk_write_max)
            SELECT $ts,
                   SUM(cpu_x10), COUNT(cpu_x10), MAX(cpu_x10),
                   SUM(mem_x10), COUNT(mem_x10), MAX(mem_x10),
                   SUM(net_in_bps), COUNT(net_in_bps), MAX(net_in_bps),
                   SUM(net_out_bps), COUNT(net_out_bps), MAX(net_out_bps),
                   SUM(cpu_temp_x10), COUNT(cpu_temp_x10), MAX(cpu_temp_x10),
                   SUM(disk_read_bps), COUNT(disk_read_bps), MAX(disk_read_bps),
                   SUM(disk_write_bps), COUNT(disk_write_bps), MAX(disk_write_bps)
            FROM metric_seconds
            WHERE ts >= $ts AND ts < $ts + 60;
        """;
        var pTs = AddParam(cmd, "$ts");
        foreach (var minute in minutes)
        {
            pTs.Value = minute;
            cmd.ExecuteNonQuery();
        }
    }

    private void UpsertGpuRollup(SqliteTransaction tx, IReadOnlyList<MetricSample> samples, Dictionary<string, long> pendingGpuKeys)
    {
        var minuteGpuPairs = new HashSet<(long Minute, long GpuKey)>();
        foreach (var s in samples)
        {
            var minute = s.TsSec / 60 * 60;
            foreach (var g in s.Gpus)
            {
                minuteGpuPairs.Add((minute, ResolveKnownKey(g.GpuId, _gpuKeys, pendingGpuKeys)));
            }
        }
        if (minuteGpuPairs.Count == 0)
        {
            return;
        }

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO gpu_minutes (ts_min, gpu, load_sum_x10, load_cnt, load_max_x10, temp_sum_x10, temp_cnt, temp_max_x10)
            SELECT $ts, $gpu,
                   SUM(load_x10), COUNT(load_x10), MAX(load_x10),
                   SUM(temp_x10), COUNT(temp_x10), MAX(temp_x10)
            FROM gpu_seconds
            WHERE gpu = $gpu AND ts >= $ts AND ts < $ts + 60;
        """;
        var pTs = AddParam(cmd, "$ts");
        var pGpu = AddParam(cmd, "$gpu");
        foreach (var (minute, gpuKey) in minuteGpuPairs)
        {
            pTs.Value = minute;
            pGpu.Value = gpuKey;
            cmd.ExecuteNonQuery();
        }
    }

    private void UpsertFanRollup(SqliteTransaction tx, IReadOnlyList<MetricSample> samples, Dictionary<string, long> pendingFanKeys)
    {
        var minuteFanPairs = new HashSet<(long Minute, long FanKey)>();
        foreach (var s in samples)
        {
            var minute = s.TsSec / 60 * 60;
            foreach (var f in s.Fans)
            {
                minuteFanPairs.Add((minute, ResolveKnownKey(f.FanId, _fanKeys, pendingFanKeys)));
            }
        }
        if (minuteFanPairs.Count == 0)
        {
            return;
        }

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO fan_minutes (ts_min, fan, rpm_sum, rpm_cnt, rpm_max, duty_sum, duty_cnt, duty_max)
            SELECT $ts, $fan,
                   SUM(rpm), COUNT(rpm), MAX(rpm),
                   SUM(duty), COUNT(duty), MAX(duty)
            FROM fan_seconds
            WHERE fan = $fan AND ts >= $ts AND ts < $ts + 60;
        """;
        var pTs = AddParam(cmd, "$ts");
        var pFan = AddParam(cmd, "$fan");
        foreach (var (minute, fanKey) in minuteFanPairs)
        {
            pTs.Value = minute;
            pFan.Value = fanKey;
            cmd.ExecuteNonQuery();
        }
    }

    private const long TempBucketSeconds = MetricsHistory.TempBucketMinutes * 60L;

    // Unifies cpu (metric_seconds) + gpu (gpu_seconds/gpu_series) + storage/
    // ram (temp_component_seconds/temp_component_series) into the one
    // long-retention temp_buckets table, rebuilt (never accumulated) per
    // touched bucket like the sibling *_minutes rollups above (just at
    // TempBucketSeconds width instead of a minute) - idempotent no matter how
    // many times a given bucket is touched, but ONLY within source retention
    // (see _sourceFloorSec): a bucket older than that is skipped rather than
    // rebuilt, since metric_seconds/gpu_seconds/temp_component_seconds no
    // longer hold its full window and a rebuild there would replace a
    // complete 90-day aggregate with whatever fragment (often a single
    // out-of-order sample) source still has. A HAVING count>0 guard keeps a
    // bucket with no reading for a given component from writing an empty row
    // (the component's series/key still exists; it just has nothing to
    // report that bucket).
    private void UpsertTempBucketsRollup(
        SqliteTransaction tx, IReadOnlyList<MetricSample> samples,
        Dictionary<string, long> pendingGpuKeys, Dictionary<string, long> pendingTempComponentKeys)
    {
        var floor = _sourceFloorSec;
        bool WithinSourceRetention(long bucketStart) => floor is not { } f || bucketStart >= f;

        var buckets = new HashSet<long>();
        foreach (var s in samples)
        {
            var bucket = s.TsSec / TempBucketSeconds * TempBucketSeconds;
            if (WithinSourceRetention(bucket))
            {
                buckets.Add(bucket);
            }
        }
        if (buckets.Count == 0)
        {
            return;
        }

        var cpuName = "CPU";
        foreach (var s in samples)
        {
            if (!string.IsNullOrEmpty(s.CpuName))
            {
                cpuName = s.CpuName;
                break;
            }
        }

        using var cpuCmd = _connection.CreateCommand();
        cpuCmd.Transaction = tx;
        cpuCmd.CommandText = """
            INSERT OR REPLACE INTO temp_buckets (bucket_ts, component_id, kind, name, sum_x10, cnt, max_x10)
            SELECT $ts, 'cpu', 'cpu', $name, SUM(cpu_temp_x10), COUNT(cpu_temp_x10), MAX(cpu_temp_x10)
            FROM metric_seconds
            WHERE ts >= $ts AND ts < $ts + $width
            HAVING COUNT(cpu_temp_x10) > 0;
        """;
        var cpuTs = AddParam(cpuCmd, "$ts");
        var cpuNameParam = AddParam(cpuCmd, "$name");
        cpuNameParam.Value = cpuName;
        AddParam(cpuCmd, "$width").Value = TempBucketSeconds;

        using var gpuCmd = _connection.CreateCommand();
        gpuCmd.Transaction = tx;
        gpuCmd.CommandText = """
            INSERT OR REPLACE INTO temp_buckets (bucket_ts, component_id, kind, name, sum_x10, cnt, max_x10)
            SELECT $ts, 'gpu:' || se.gpu_id, 'gpu', se.name,
                   SUM(gs.temp_x10), COUNT(gs.temp_x10), MAX(gs.temp_x10)
            FROM gpu_seconds gs
            JOIN gpu_series se ON se.key = gs.gpu
            WHERE gs.gpu = $gpu AND gs.ts >= $ts AND gs.ts < $ts + $width
            GROUP BY se.gpu_id, se.name
            HAVING COUNT(gs.temp_x10) > 0;
        """;
        var gpuTs = AddParam(gpuCmd, "$ts");
        var gpuKeyParam = AddParam(gpuCmd, "$gpu");
        AddParam(gpuCmd, "$width").Value = TempBucketSeconds;

        using var compCmd = _connection.CreateCommand();
        compCmd.Transaction = tx;
        compCmd.CommandText = """
            INSERT OR REPLACE INTO temp_buckets (bucket_ts, component_id, kind, name, sum_x10, cnt, max_x10)
            SELECT $ts, se.component_id, se.kind, se.name,
                   SUM(cs.value_x10), COUNT(cs.value_x10), MAX(cs.value_x10)
            FROM temp_component_seconds cs
            JOIN temp_component_series se ON se.key = cs.component
            WHERE cs.component = $component AND cs.ts >= $ts AND cs.ts < $ts + $width
            GROUP BY se.component_id, se.kind, se.name
            HAVING COUNT(cs.value_x10) > 0;
        """;
        var compTs = AddParam(compCmd, "$ts");
        var compKeyParam = AddParam(compCmd, "$component");
        AddParam(compCmd, "$width").Value = TempBucketSeconds;

        var bucketGpuPairs = new HashSet<(long Bucket, long GpuKey)>();
        var bucketComponentPairs = new HashSet<(long Bucket, long ComponentKey)>();
        foreach (var s in samples)
        {
            var bucket = s.TsSec / TempBucketSeconds * TempBucketSeconds;
            if (!WithinSourceRetention(bucket))
            {
                continue;
            }
            foreach (var g in s.Gpus)
            {
                bucketGpuPairs.Add((bucket, ResolveKnownKey(g.GpuId, _gpuKeys, pendingGpuKeys)));
            }
            foreach (var c in s.ComponentTemps)
            {
                bucketComponentPairs.Add((bucket, ResolveKnownKey(c.ComponentId, _tempComponentKeys, pendingTempComponentKeys)));
            }
        }

        foreach (var bucket in buckets)
        {
            cpuTs.Value = bucket;
            cpuCmd.ExecuteNonQuery();
        }
        foreach (var (bucket, gpuKey) in bucketGpuPairs)
        {
            gpuTs.Value = bucket;
            gpuKeyParam.Value = gpuKey;
            gpuCmd.ExecuteNonQuery();
        }
        foreach (var (bucket, componentKey) in bucketComponentPairs)
        {
            compTs.Value = bucket;
            compKeyParam.Value = componentKey;
            compCmd.ExecuteNonQuery();
        }
    }

    private void Prune(SqliteTransaction tx, long cutoffSec)
    {
        foreach (var table in new[] { "metric_seconds", "gpu_seconds", "fan_seconds", "temp_component_seconds" })
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE ts < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoffSec);
            cmd.ExecuteNonQuery();
        }

        // Raised, never lowered: a later cutoffSec smaller than the current
        // floor (a backward system clock step feeding MetricsSampler.Flush's
        // own "now") must not reopen buckets this same field already ruled
        // unsafe to rebuild.
        _sourceFloorSec = Math.Max(_sourceFloorSec ?? long.MinValue, cutoffSec);

        foreach (var (table, tsColumn) in RollupTables)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE {tsColumn} < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoffSec);
            cmd.ExecuteNonQuery();
        }

        // temp_buckets keeps MetricsHistory.TempRetentionDays instead of the
        // RetentionDays cutoffSec already encodes ("now - RetentionDays"), so
        // shift it back by the gap between the two retention windows rather
        // than threading a second now-relative timestamp through Append.
        var tempCutoffSec = cutoffSec - (MetricsHistory.TempRetentionDays - MetricsHistory.RetentionDays) * 86_400L;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM temp_buckets WHERE bucket_ts < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", tempCutoffSec);
            cmd.ExecuteNonQuery();
        }
    }

    private static readonly (string Table, string TsColumn)[] RollupTables =
    {
        ("metric_minutes", "ts_min"), ("gpu_minutes", "ts_min"), ("fan_minutes", "ts_min"),
    };

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

    private long ResolveTempComponentKey(
        SqliteTransaction tx, string componentId, string kind, string name, Dictionary<string, long> pendingKeys)
    {
        if (_tempComponentKeys.TryGetValue(componentId, out var cached))
        {
            return cached;
        }
        if (pendingKeys.TryGetValue(componentId, out var pending))
        {
            return pending;
        }

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO temp_component_series (component_id, kind, name) VALUES ($id, $kind, $name)
            ON CONFLICT(component_id) DO UPDATE SET kind = excluded.kind, name = excluded.name
            RETURNING key;
        """;
        cmd.Parameters.AddWithValue("$id", componentId);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$name", name);
        var key = Convert.ToInt64(cmd.ExecuteScalar());
        pendingKeys[componentId] = key;
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
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT component_id, key FROM temp_component_series;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                _tempComponentKeys[reader.GetString(0)] = reader.GetInt64(1);
            }
        }
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name, key FROM app_series;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                _appKeys[reader.GetString(0)] = reader.GetInt64(1);
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
                ts             INTEGER PRIMARY KEY,
                cpu_x10        INTEGER,
                mem_x10        INTEGER,
                net_in_bps     INTEGER,
                net_out_bps    INTEGER,
                cpu_temp_x10   INTEGER,
                disk_read_bps  INTEGER,
                disk_write_bps INTEGER
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

            CREATE TABLE IF NOT EXISTS metric_minutes (
                ts_min           INTEGER PRIMARY KEY,
                cpu_sum_x10      INTEGER, cpu_cnt INTEGER NOT NULL DEFAULT 0, cpu_max_x10 INTEGER,
                mem_sum_x10      INTEGER, mem_cnt INTEGER NOT NULL DEFAULT 0, mem_max_x10 INTEGER,
                net_in_sum       INTEGER, net_in_cnt INTEGER NOT NULL DEFAULT 0, net_in_max INTEGER,
                net_out_sum      INTEGER, net_out_cnt INTEGER NOT NULL DEFAULT 0, net_out_max INTEGER,
                cpu_temp_sum_x10 INTEGER, cpu_temp_cnt INTEGER NOT NULL DEFAULT 0, cpu_temp_max_x10 INTEGER,
                disk_read_sum    INTEGER, disk_read_cnt INTEGER NOT NULL DEFAULT 0, disk_read_max INTEGER,
                disk_write_sum   INTEGER, disk_write_cnt INTEGER NOT NULL DEFAULT 0, disk_write_max INTEGER
            );

            CREATE TABLE IF NOT EXISTS gpu_minutes (
                ts_min       INTEGER NOT NULL,
                gpu          INTEGER NOT NULL,
                load_sum_x10 INTEGER, load_cnt INTEGER NOT NULL DEFAULT 0, load_max_x10 INTEGER,
                temp_sum_x10 INTEGER, temp_cnt INTEGER NOT NULL DEFAULT 0, temp_max_x10 INTEGER,
                PRIMARY KEY (ts_min, gpu)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS fan_minutes (
                ts_min   INTEGER NOT NULL,
                fan      INTEGER NOT NULL,
                rpm_sum  INTEGER, rpm_cnt INTEGER NOT NULL DEFAULT 0, rpm_max INTEGER,
                duty_sum INTEGER, duty_cnt INTEGER NOT NULL DEFAULT 0, duty_max INTEGER,
                PRIMARY KEY (ts_min, fan)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS temp_component_series (
                key          INTEGER PRIMARY KEY AUTOINCREMENT,
                component_id TEXT NOT NULL UNIQUE,
                kind         TEXT NOT NULL,
                name         TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS temp_component_seconds (
                ts        INTEGER NOT NULL,
                component INTEGER NOT NULL,
                value_x10 INTEGER,
                PRIMARY KEY (ts, component)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS temp_buckets (
                bucket_ts    INTEGER NOT NULL,
                component_id TEXT    NOT NULL,
                kind         TEXT    NOT NULL,
                name         TEXT    NOT NULL,
                sum_x10      INTEGER NOT NULL,
                cnt          INTEGER NOT NULL,
                max_x10      INTEGER NOT NULL,
                PRIMARY KEY (bucket_ts, component_id)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_temp_buckets_ts ON temp_buckets(bucket_ts);

            CREATE TABLE IF NOT EXISTS app_series (
                key  INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE
            );

            CREATE TABLE IF NOT EXISTS app_cpu_seconds (
                ts        INTEGER NOT NULL,
                app       INTEGER NOT NULL,
                value_x10 INTEGER,
                PRIMARY KEY (ts, app)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_app_cpu_seconds_app_ts ON app_cpu_seconds(app, ts);

            CREATE TABLE IF NOT EXISTS app_mem_seconds (
                ts        INTEGER NOT NULL,
                app       INTEGER NOT NULL,
                value_x10 INTEGER,
                PRIMARY KEY (ts, app)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_app_mem_seconds_app_ts ON app_mem_seconds(app, ts);

            CREATE TABLE IF NOT EXISTS app_gpu_seconds (
                ts        INTEGER NOT NULL,
                app       INTEGER NOT NULL,
                gpu       INTEGER NOT NULL,
                value_x10 INTEGER,
                vram_mb   INTEGER,
                PRIMARY KEY (ts, app, gpu)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_app_gpu_seconds_gpu_app_ts ON app_gpu_seconds(gpu, app, ts);

            CREATE TABLE IF NOT EXISTS app_vram_seconds (
                ts       INTEGER NOT NULL,
                app      INTEGER NOT NULL,
                gpu      INTEGER NOT NULL,
                value_mb INTEGER,
                PRIMARY KEY (ts, app, gpu)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_app_vram_seconds_gpu_app_ts ON app_vram_seconds(gpu, app, ts);

            CREATE TABLE IF NOT EXISTS privacy_sessions (
                app_id     TEXT NOT NULL,
                capability TEXT NOT NULL,
                start_utc  INTEGER NOT NULL,
                end_utc    INTEGER,
                UNIQUE(app_id, capability, start_utc)
            );
            CREATE INDEX IF NOT EXISTS ix_privacy_sessions_start ON privacy_sessions(start_utc);

            CREATE TABLE IF NOT EXISTS schema_meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT OR IGNORE INTO schema_meta (key, value) VALUES ('version', '1');
        """;
        cmd.ExecuteNonQuery();

        MigrateAppSeriesToNoCase();
        ImportLegacyTemperatureHistory();
        MigrateAddDiskColumns();
    }

    // Runs once (schema_meta version gate): imports the retired
    // temperature.db's temp_buckets rows into this store's own temp_buckets
    // table (see LegacyTemperatureImportMigration for the column mapping and
    // the accepted GPU-id discontinuity), then leaves temperature.db in place
    // untouched - a partial or failed import always has the source data to
    // retry from on the next boot. A box with no temperature.db (fresh
    // install, or one that already ran this) has nothing to import and just
    // bumps the version.
    private const int SchemaVersionTempImport = 3;

    private void ImportLegacyTemperatureHistory()
    {
        if (ReadSchemaVersion() >= SchemaVersionTempImport)
        {
            return;
        }

        try
        {
            var legacyPath = ResolveLegacyTemperatureDbPath();
            if (File.Exists(legacyPath))
            {
                LegacyTemperatureImportMigration.Run(_connection, legacyPath);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[metrics-history-store] legacy temperature import failed (retrying next boot): {ex.GetType().Name}: {ex.Message}");
            return;
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO schema_meta (key, value) VALUES ('version', $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
        """;
        cmd.Parameters.AddWithValue("$v", SchemaVersionTempImport.ToString());
        cmd.ExecuteNonQuery();
    }

    private string ResolveLegacyTemperatureDbPath() =>
        Path.Combine(Path.GetDirectoryName(_dbPath)!, "temperature.db");

    // disk_read_bps/disk_write_bps (metric_seconds) and their sum/cnt/max
    // rollup counterparts (metric_minutes) were added after this store's
    // initial release; CREATE TABLE IF NOT EXISTS above no-ops on a
    // database that already has these tables, so an existing box needs the
    // columns added explicitly. Gated by ColumnExists (checked, and skipped,
    // on every boot) rather than the shared schema_meta version counter: that
    // counter is a single monotonic value shared by every migration in this
    // file, and bumping it here would make an earlier, still-pending
    // migration's own "< threshold" gate (e.g. ImportLegacyTemperatureHistory's
    // retry-on-failure contract) see a version past its threshold and wrongly
    // treat itself as already done.
    private void MigrateAddDiskColumns()
    {
        AddColumnIfMissing("metric_seconds", "disk_read_bps", "INTEGER");
        AddColumnIfMissing("metric_seconds", "disk_write_bps", "INTEGER");
        AddColumnIfMissing("metric_minutes", "disk_read_sum", "INTEGER");
        AddColumnIfMissing("metric_minutes", "disk_read_cnt", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("metric_minutes", "disk_read_max", "INTEGER");
        AddColumnIfMissing("metric_minutes", "disk_write_sum", "INTEGER");
        AddColumnIfMissing("metric_minutes", "disk_write_cnt", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("metric_minutes", "disk_write_max", "INTEGER");
    }

    private bool ColumnExists(string table, string column)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private void AddColumnIfMissing(string table, string column, string columnDdl)
    {
        if (ColumnExists(table, column))
        {
            return;
        }
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnDdl};";
        cmd.ExecuteNonQuery();
    }

    // app_series was created without COLLATE NOCASE by an earlier version
    // of this store; CREATE TABLE IF NOT EXISTS above no-ops on a database
    // that already has the table, so a box that ran that version keeps a
    // case-sensitive app_series forever without this. Rebuilds app_series
    // with the NOCASE collation, merging any case-duplicate rows (first-seen
    // casing wins) and repointing every app_*_seconds row at the merged key.
    private const int SchemaVersionAppSeriesNoCase = 2;

    private void MigrateAppSeriesToNoCase()
    {
        if (ReadSchemaVersion() >= SchemaVersionAppSeriesNoCase)
        {
            return;
        }

        using (var tx = _connection.BeginTransaction())
        {
            ExecuteNonQuery(tx, """
                CREATE TABLE app_series_migrated (
                    key  INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL COLLATE NOCASE UNIQUE
                );
            """);
            // Ordered by key ascending, so the lowest (earliest-inserted)
            // key for a case-insensitive name wins the NOCASE UNIQUE
            // conflict and every later duplicate is skipped, preserving
            // first-seen casing.
            ExecuteNonQuery(tx, """
                INSERT OR IGNORE INTO app_series_migrated (name)
                SELECT name FROM app_series ORDER BY key ASC;
            """);
            ExecuteNonQuery(tx, """
                CREATE TEMP TABLE app_key_migration_map AS
                SELECT old_series.key AS old_key, new_series.key AS new_key
                FROM app_series old_series
                JOIN app_series_migrated new_series ON old_series.name = new_series.name COLLATE NOCASE;
            """);

            MigrateAppSecondsTable(tx, "app_cpu_seconds",
                "ts        INTEGER NOT NULL, app INTEGER NOT NULL, value_x10 INTEGER, PRIMARY KEY (ts, app)",
                "ts, app, value_x10", "s.ts, m.new_key, s.value_x10");
            MigrateAppSecondsTable(tx, "app_mem_seconds",
                "ts        INTEGER NOT NULL, app INTEGER NOT NULL, value_x10 INTEGER, PRIMARY KEY (ts, app)",
                "ts, app, value_x10", "s.ts, m.new_key, s.value_x10");
            MigrateAppSecondsTable(tx, "app_gpu_seconds",
                "ts INTEGER NOT NULL, app INTEGER NOT NULL, gpu INTEGER NOT NULL, value_x10 INTEGER, vram_mb INTEGER, PRIMARY KEY (ts, app, gpu)",
                "ts, app, gpu, value_x10, vram_mb", "s.ts, m.new_key, s.gpu, s.value_x10, s.vram_mb");

            ExecuteNonQuery(tx, "DROP TABLE app_series;");
            ExecuteNonQuery(tx, "ALTER TABLE app_series_migrated RENAME TO app_series;");
            ExecuteNonQuery(tx, "DROP TABLE app_key_migration_map;");

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO schema_meta (key, value) VALUES ('version', $v)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
                cmd.Parameters.AddWithValue("$v", SchemaVersionAppSeriesNoCase.ToString());
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        // The app_*_seconds tables were dropped and rebuilt above, taking
        // their indexes with them; EnsureSchema's own CREATE INDEX IF NOT
        // EXISTS calls already ran against the now-gone originals, so this
        // restores them on the rebuilt tables.
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE INDEX IF NOT EXISTS ix_app_cpu_seconds_app_ts ON app_cpu_seconds(app, ts);
                CREATE INDEX IF NOT EXISTS ix_app_mem_seconds_app_ts ON app_mem_seconds(app, ts);
                CREATE INDEX IF NOT EXISTS ix_app_gpu_seconds_gpu_app_ts ON app_gpu_seconds(gpu, app, ts);
            """;
            cmd.ExecuteNonQuery();
        }
    }

    // A repointed (ts, app) or (ts, app, gpu) pair can collide when two
    // differently-cased rows for the same app landed at the same tick -
    // INSERT OR REPLACE keeps whichever the SELECT visits last, an
    // arbitrary but deterministic resolution for what is already an
    // ambiguous duplicate reading.
    private void MigrateAppSecondsTable(SqliteTransaction tx, string table, string columnsDdl, string columns, string selectExpr)
    {
        var migratedTable = table + "_migrated";
        ExecuteNonQuery(tx, $"CREATE TABLE {migratedTable} ({columnsDdl}) WITHOUT ROWID;");
        ExecuteNonQuery(tx, $"""
            INSERT OR REPLACE INTO {migratedTable} ({columns})
            SELECT {selectExpr}
            FROM {table} s
            JOIN app_key_migration_map m ON s.app = m.old_key;
        """);
        ExecuteNonQuery(tx, $"DROP TABLE {table};");
        ExecuteNonQuery(tx, $"ALTER TABLE {migratedTable} RENAME TO {table};");
    }

    private int ReadSchemaVersion()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM schema_meta WHERE key = 'version';";
        var result = cmd.ExecuteScalar();
        return result is string s && int.TryParse(s, out var v) ? v : 1;
    }

    private void ExecuteNonQuery(SqliteTransaction tx, string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Upsert(string capability, string appId, long startUtcSec, long? endUtcSec)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO privacy_sessions (app_id, capability, start_utc, end_utc)
                VALUES ($app, $cap, $start, $end)
                ON CONFLICT(app_id, capability, start_utc) DO UPDATE SET end_utc = excluded.end_utc;
            """;
            cmd.Parameters.AddWithValue("$app", appId);
            cmd.Parameters.AddWithValue("$cap", capability);
            cmd.Parameters.AddWithValue("$start", startUtcSec);
            cmd.Parameters.AddWithValue("$end", (object?)endUtcSec ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    IReadOnlyList<PrivacySession> IPrivacySessionStore.Query(long fromSec, long toSec)
    {
        lock (_writeLock)
        {
            var result = new List<PrivacySession>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT app_id, capability, start_utc, end_utc
                FROM privacy_sessions
                WHERE start_utc <= $to AND (end_utc IS NULL OR end_utc >= $from)
                ORDER BY start_utc ASC;
            """;
            cmd.Parameters.AddWithValue("$from", fromSec);
            cmd.Parameters.AddWithValue("$to", toSec);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new PrivacySession(
                    reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3)));
            }
            return result;
        }
    }

    void IPrivacySessionStore.PruneOlderThan(long cutoffSec)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            // An open row (end_utc NULL) past retention by its start is
            // pruned too - a safety net for a session that never got a
            // proper close recorded (PrivacyAccessTransitions handles the
            // reachable cases directly; this is the backstop for any it
            // doesn't, e.g. an orphan already on disk from before that fix).
            cmd.CommandText = """
                DELETE FROM privacy_sessions
                WHERE (end_utc IS NOT NULL AND end_utc < $cutoff)
                   OR (end_utc IS NULL AND start_utc < $cutoff);
            """;
            cmd.Parameters.AddWithValue("$cutoff", cutoffSec);
            cmd.ExecuteNonQuery();
        }
    }

    // ----- IAppUsageHistoryStore -----

    public void Append(IReadOnlyList<AppUsageTick> ticks, long? pruneCutoffSec)
    {
        if (ticks.Count == 0 && pruneCutoffSec is null)
        {
            return;
        }

        lock (_writeLock)
        {
            using var tx = _connection.BeginTransaction();
            var pendingAppKeys = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<long>? orphanedAppKeys = null;

            if (ticks.Count > 0)
            {
                InsertAppRows(tx, ticks, pendingAppKeys);
            }

            if (pruneCutoffSec is { } cutoff)
            {
                orphanedAppKeys = PruneApps(tx, cutoff);
            }

            tx.Commit();

            foreach (var (name, key) in pendingAppKeys)
            {
                _appKeys[name] = key;
            }

            // A cache entry surviving past its app_series row's deletion
            // would resolve future inserts for that name to a dangling app
            // key, making them invisible to QueryTopApps's JOIN.
            if (orphanedAppKeys is { Count: > 0 })
            {
                var evicted = new HashSet<long>(orphanedAppKeys);
                foreach (var name in _appKeys.Where(kv => evicted.Contains(kv.Value)).Select(kv => kv.Key).ToList())
                {
                    _appKeys.Remove(name);
                }
            }
        }
    }

    private void InsertAppRows(SqliteTransaction tx, IReadOnlyList<AppUsageTick> ticks, Dictionary<string, long> pendingAppKeys)
    {
        using var cpuCmd = _connection.CreateCommand();
        cpuCmd.Transaction = tx;
        cpuCmd.CommandText = "INSERT OR REPLACE INTO app_cpu_seconds (ts, app, value_x10) VALUES ($ts, $app, $value);";
        var cpuTs = AddParam(cpuCmd, "$ts");
        var cpuApp = AddParam(cpuCmd, "$app");
        var cpuValue = AddParam(cpuCmd, "$value");

        using var memCmd = _connection.CreateCommand();
        memCmd.Transaction = tx;
        memCmd.CommandText = "INSERT OR REPLACE INTO app_mem_seconds (ts, app, value_x10) VALUES ($ts, $app, $value);";
        var memTs = AddParam(memCmd, "$ts");
        var memApp = AddParam(memCmd, "$app");
        var memValue = AddParam(memCmd, "$value");

        using var gpuCmd = _connection.CreateCommand();
        gpuCmd.Transaction = tx;
        gpuCmd.CommandText = "INSERT OR REPLACE INTO app_gpu_seconds (ts, app, gpu, value_x10, vram_mb) VALUES ($ts, $app, $gpu, $value, $vram);";
        var gpuTs = AddParam(gpuCmd, "$ts");
        var gpuApp = AddParam(gpuCmd, "$app");
        var gpuGpu = AddParam(gpuCmd, "$gpu");
        var gpuValue = AddParam(gpuCmd, "$value");
        var gpuVram = AddParam(gpuCmd, "$vram");

        using var vramCmd = _connection.CreateCommand();
        vramCmd.Transaction = tx;
        vramCmd.CommandText = "INSERT OR REPLACE INTO app_vram_seconds (ts, app, gpu, value_mb) VALUES ($ts, $app, $gpu, $value);";
        var vramTs = AddParam(vramCmd, "$ts");
        var vramApp = AddParam(vramCmd, "$app");
        var vramGpu = AddParam(vramCmd, "$gpu");
        var vramValue = AddParam(vramCmd, "$value");

        foreach (var tick in ticks)
        {
            foreach (var metric in tick.Metrics)
            {
                if (metric.Metric == "cpu")
                {
                    foreach (var a in metric.Apps)
                    {
                        cpuTs.Value = tick.TsSec;
                        cpuApp.Value = ResolveAppKey(tx, a.Name, pendingAppKeys);
                        cpuValue.Value = ScaleX10(a.Value);
                        cpuCmd.ExecuteNonQuery();
                    }
                }
                else if (metric.Metric == "memory")
                {
                    foreach (var a in metric.Apps)
                    {
                        memTs.Value = tick.TsSec;
                        memApp.Value = ResolveAppKey(tx, a.Name, pendingAppKeys);
                        memValue.Value = ScaleX10(a.Value);
                        memCmd.ExecuteNonQuery();
                    }
                }
                else if (metric.Metric.StartsWith("gpu:", StringComparison.Ordinal))
                {
                    var gid = metric.Metric[4..];
                    // The scalar Append that runs immediately before this one
                    // in the same flush (MetricsSampler.Flush) resolves
                    // gpu_series for every GPU that tick's scalar sample saw;
                    // a gid with no scalar row this flush has no app rows
                    // either, since both come from the same sensors.GetGpus()
                    // read - skip rather than guess a name to mint one.
                    if (!_gpuKeys.TryGetValue(gid, out var gpuKey))
                    {
                        continue;
                    }
                    foreach (var a in metric.Apps)
                    {
                        gpuTs.Value = tick.TsSec;
                        gpuApp.Value = ResolveAppKey(tx, a.Name, pendingAppKeys);
                        gpuGpu.Value = gpuKey;
                        gpuValue.Value = ScaleX10(a.Value);
                        gpuVram.Value = a.VramMb is { } v ? (object)(long)Math.Round(v) : DBNull.Value;
                        gpuCmd.ExecuteNonQuery();
                    }
                }
                else if (metric.Metric.StartsWith("vram:", StringComparison.Ordinal))
                {
                    var gid = metric.Metric[5..];
                    // Same gpu_series dependency as the "gpu:" branch above:
                    // a gid with no scalar row this flush has no app rows.
                    if (!_gpuKeys.TryGetValue(gid, out var gpuKey))
                    {
                        continue;
                    }
                    foreach (var a in metric.Apps)
                    {
                        vramTs.Value = tick.TsSec;
                        vramApp.Value = ResolveAppKey(tx, a.Name, pendingAppKeys);
                        vramGpu.Value = gpuKey;
                        vramValue.Value = ScaleWhole(a.Value);
                        vramCmd.ExecuteNonQuery();
                    }
                }
            }
        }
    }

    private static readonly string[] AppSecondsTables = { "app_cpu_seconds", "app_mem_seconds", "app_gpu_seconds", "app_vram_seconds" };

    private IReadOnlyList<long> PruneApps(SqliteTransaction tx, long cutoffSec)
    {
        foreach (var table in AppSecondsTables)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE ts < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoffSec);
            cmd.ExecuteNonQuery();
        }

        var orphaned = new List<long>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT key FROM app_series
                WHERE key NOT IN (SELECT app FROM app_cpu_seconds)
                  AND key NOT IN (SELECT app FROM app_mem_seconds)
                  AND key NOT IN (SELECT app FROM app_gpu_seconds)
                  AND key NOT IN (SELECT app FROM app_vram_seconds);
            """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                orphaned.Add(reader.GetInt64(0));
            }
        }
        if (orphaned.Count > 0)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM app_series WHERE key = $key;";
            var p = AddParam(cmd, "$key");
            foreach (var key in orphaned)
            {
                p.Value = key;
                cmd.ExecuteNonQuery();
            }
        }
        return orphaned;
    }

    private long ResolveAppKey(SqliteTransaction tx, string name, Dictionary<string, long> pendingKeys)
    {
        if (_appKeys.TryGetValue(name, out var cached))
        {
            return cached;
        }
        if (pendingKeys.TryGetValue(name, out var pending))
        {
            return pending;
        }

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        // name = app_series.name (not excluded.name): app_series.name is
        // COLLATE NOCASE, so a later differently-cased observation of the
        // same app conflicts here and must keep the first-seen casing
        // rather than overwrite it every time casing varies.
        cmd.CommandText = """
            INSERT INTO app_series (name) VALUES ($name)
            ON CONFLICT(name) DO UPDATE SET name = app_series.name
            RETURNING key;
        """;
        cmd.Parameters.AddWithValue("$name", name);
        var key = Convert.ToInt64(cmd.ExecuteScalar());
        pendingKeys[name] = key;
        return key;
    }

    public IReadOnlyList<AppWindowStat> QueryTopApps(string metric, long fromSec, long toSec, int maxApps)
    {
        lock (_writeLock)
        {
            var (table, gpuKey) = ResolveMetricTable(metric);
            if (table is null)
            {
                return Array.Empty<AppWindowStat>();
            }

            var gpuFilter = gpuKey is not null ? "gpu = $gpu AND " : "";

            // The window average must divide by how many ticks the metric
            // was actually sampled in [from, to], not by an app's own row
            // count (rows only exist for ticks the app ranked into the top
            // N) - otherwise a single spike tick outranks a lower load
            // sustained across the whole window.
            long expectedTicks;
            using (var tickCmd = _connection.CreateCommand())
            {
                tickCmd.CommandText = $"SELECT COUNT(DISTINCT ts) FROM {table} WHERE {gpuFilter}ts BETWEEN $from AND $to;";
                if (gpuKey is { } gk0)
                {
                    tickCmd.Parameters.AddWithValue("$gpu", gk0);
                }
                tickCmd.Parameters.AddWithValue("$from", fromSec);
                tickCmd.Parameters.AddWithValue("$to", toSec);
                expectedTicks = Convert.ToInt64(tickCmd.ExecuteScalar());
            }
            if (expectedTicks == 0)
            {
                return Array.Empty<AppWindowStat>();
            }

            // app_gpu_seconds/app_vram_seconds carry a gpu dimension: a
            // bare query (gpuFilter empty) can have more than one adapter's
            // row per (app, ts); pre-aggregating in the "pt" subquery
            // collapses those to one summed value per tick before ranking,
            // so MAX reflects the combined-adapter peak in a single tick
            // rather than one adapter's peak in isolation. A specific
            // gpu:<id>/vram:<id> query already has at most one row per
            // (app, ts), so the pre-aggregation is a no-op there; cpu/memory
            // never have an adapter dimension and keep the direct query.
            // value_mb (vram) stores whole MB, unlike value_x10's
            // fixed-point percent encoding, so it needs no unscale divisor.
            var isMultiAdapter = table is "app_gpu_seconds" or "app_vram_seconds";
            var valueColumn = table == "app_vram_seconds" ? "value_mb" : "value_x10";
            var scaleSql = table == "app_vram_seconds" ? "1.0" : "10.0";
            var scaleDivisor = table == "app_vram_seconds" ? 1.0 : 10.0;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = isMultiAdapter
                ? $"""
                    SELECT se.name, SUM(pt.v), MAX(pt.v) / {scaleSql}
                    FROM (
                        SELECT ts, app, SUM({valueColumn}) AS v
                        FROM {table}
                        WHERE {gpuFilter}ts BETWEEN $from AND $to
                        GROUP BY ts, app
                    ) pt
                    JOIN app_series se ON se.key = pt.app
                    GROUP BY pt.app
                    ORDER BY SUM(pt.v) DESC
                    LIMIT $limit;
                """
                : $"""
                    SELECT se.name, SUM(t.{valueColumn}), MAX(t.{valueColumn}) / {scaleSql}
                    FROM {table} t
                    JOIN app_series se ON se.key = t.app
                    WHERE {gpuFilter}t.ts BETWEEN $from AND $to
                    GROUP BY t.app
                    ORDER BY SUM(t.{valueColumn}) DESC
                    LIMIT $limit;
                """;
            if (gpuKey is { } gk)
            {
                cmd.Parameters.AddWithValue("$gpu", gk);
            }
            cmd.Parameters.AddWithValue("$from", fromSec);
            cmd.Parameters.AddWithValue("$to", toSec);
            cmd.Parameters.AddWithValue("$limit", maxApps);

            var result = new List<AppWindowStat>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var sum = reader.GetInt64(1);
                result.Add(new AppWindowStat(reader.GetString(0), sum / (expectedTicks * scaleDivisor), reader.GetDouble(2)));
            }
            return result;
        }
    }

    public IReadOnlyList<long> QuerySampledTicks(string metric, long fromSec, long toSec)
    {
        lock (_writeLock)
        {
            var (table, gpuKey) = ResolveMetricTable(metric);
            if (table is null)
            {
                return Array.Empty<long>();
            }

            var gpuFilter = gpuKey is not null ? "gpu = $gpu AND " : "";
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"SELECT DISTINCT ts FROM {table} WHERE {gpuFilter}ts BETWEEN $from AND $to ORDER BY ts;";
            if (gpuKey is { } gk)
            {
                cmd.Parameters.AddWithValue("$gpu", gk);
            }
            cmd.Parameters.AddWithValue("$from", fromSec);
            cmd.Parameters.AddWithValue("$to", toSec);

            var result = new List<long>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(reader.GetInt64(0));
            }
            return result;
        }
    }

    public IReadOnlyList<AppRawPoint> QueryAppSeries(string metric, string appName, long fromSec, long toSec)
    {
        lock (_writeLock)
        {
            var (table, gpuKey) = ResolveMetricTable(metric);
            if (table is null || !_appKeys.TryGetValue(appName, out var appKey))
            {
                return Array.Empty<AppRawPoint>();
            }

            var isGpu = table == "app_gpu_seconds";
            var isVram = table == "app_vram_seconds";
            var gpuFilter = gpuKey is not null ? "gpu = $gpu AND " : "";

            using var cmd = _connection.CreateCommand();
            // See QueryTopApps: a bare-gpu/bare-vram query (gpuFilter empty)
            // can have more than one adapter's row per (app, ts), so GROUP BY
            // ts sums them into the single point per tick the caller
            // expects. A specific gpu:<id>/vram:<id> query already has at
            // most one row per ts, so the grouping is a no-op there.
            cmd.CommandText = isGpu
                ? $"""
                    SELECT ts, SUM(value_x10), SUM(vram_mb)
                    FROM app_gpu_seconds
                    WHERE {gpuFilter}app = $app AND ts BETWEEN $from AND $to
                    GROUP BY ts
                    ORDER BY ts ASC;
                """
                : isVram
                ? $"""
                    SELECT ts, SUM(value_mb)
                    FROM app_vram_seconds
                    WHERE {gpuFilter}app = $app AND ts BETWEEN $from AND $to
                    GROUP BY ts
                    ORDER BY ts ASC;
                """
                : $"""
                    SELECT ts, value_x10
                    FROM {table}
                    WHERE app = $app AND ts BETWEEN $from AND $to
                    ORDER BY ts ASC;
                """;
            if (gpuKey is { } gk)
            {
                cmd.Parameters.AddWithValue("$gpu", gk);
            }
            cmd.Parameters.AddWithValue("$app", appKey);
            cmd.Parameters.AddWithValue("$from", fromSec);
            cmd.Parameters.AddWithValue("$to", toSec);

            var result = new List<AppRawPoint>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (isVram)
                {
                    double? mb = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                    result.Add(new AppRawPoint(reader.GetInt64(0), mb, null));
                    continue;
                }

                var value = UnscaleX10(reader, 1);
                double? vram = isGpu && !reader.IsDBNull(2) ? reader.GetInt32(2) : null;
                result.Add(new AppRawPoint(reader.GetInt64(0), value, vram));
            }
            return result;
        }
    }

    public long? QueryFirstSeen(string appName)
    {
        lock (_writeLock)
        {
            if (!_appKeys.TryGetValue(appName, out var appKey))
            {
                return null;
            }

            using var cmd = _connection.CreateCommand();
            // MIN(ts) over a table with no rows for this app yields NULL, not
            // zero rows - the outer MIN(ts) ignores those and only comes back
            // NULL itself when the app is in none of the four tables.
            cmd.CommandText = """
                SELECT MIN(ts) FROM (
                    SELECT MIN(ts) AS ts FROM app_cpu_seconds WHERE app = $app
                    UNION ALL
                    SELECT MIN(ts) FROM app_mem_seconds WHERE app = $app
                    UNION ALL
                    SELECT MIN(ts) FROM app_gpu_seconds WHERE app = $app
                    UNION ALL
                    SELECT MIN(ts) FROM app_vram_seconds WHERE app = $app
                );
            """;
            cmd.Parameters.AddWithValue("$app", appKey);
            var result = cmd.ExecuteScalar();
            return result is null or DBNull ? null : Convert.ToInt64(result);
        }
    }

    // "cpu"/"memory" resolve directly; "gpu:<gid>"/"vram:<gid>" resolve
    // through the existing gpu_series cache so app rows and scalar gpu rows
    // always agree on which surrogate key a gid maps to. A gid never seen by
    // the scalar path (no gpu_series row) yields a null table - there is
    // nothing to query, not an error. Bare "gpu"/"vram" (no adapter id) also
    // resolve to their table with a null key, the same way cpu/memory never
    // filter by adapter - QueryTopApps/QueryAppSeries then aggregate across
    // every adapter per app instead of filtering to one.
    private (string? Table, long? GpuKey) ResolveMetricTable(string metric)
    {
        if (metric == "cpu")
        {
            return ("app_cpu_seconds", null);
        }
        if (metric == "memory")
        {
            return ("app_mem_seconds", null);
        }
        if (metric == "gpu")
        {
            return ("app_gpu_seconds", null);
        }
        if (metric.StartsWith("gpu:", StringComparison.Ordinal))
        {
            var gid = metric[4..];
            return _gpuKeys.TryGetValue(gid, out var key) ? ("app_gpu_seconds", key) : (null, null);
        }
        if (metric == "vram")
        {
            return ("app_vram_seconds", null);
        }
        if (metric.StartsWith("vram:", StringComparison.Ordinal))
        {
            var gid = metric[5..];
            return _gpuKeys.TryGetValue(gid, out var key) ? ("app_vram_seconds", key) : (null, null);
        }
        return (null, null);
    }

    private static string ResolveDatabasePath() =>
        Path.Combine(NexusDataPaths.DatabaseDir(), "metrics.db");
}
