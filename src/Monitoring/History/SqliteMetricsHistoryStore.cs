using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// never repeat a string id. privacy_sessions is unrelated to 1Hz sampling
/// (PrivacyAccessWatcher writes it, on its own transition-driven cadence)
/// but shares this connection and lock since its write rate is low.
///
/// app_cpu_seconds / app_mem_seconds / app_gpu_seconds are per-app usage
/// history, sampled on MetricsHistory.AppSampleIntervalSeconds's sub-cadence:
/// three narrow tables, one per metric, instead of a single (ts, app, metric)
/// table - avoids a repeated TEXT metric discriminator per row at the row
/// counts this table reaches (MetricsHistory.TopAppsPerSample apps per
/// metric per tick, retained for MetricsHistory.RetentionDays), and lets
/// each metric's read query hit a purpose-built PK/index with no filter
/// predicate on metric. app_gpu_seconds reuses gpu_series' surrogate key
/// rather than minting a duplicate GPU id mapping.
/// </summary>
public sealed class SqliteMetricsHistoryStore : IMetricsHistoryStore, IPrivacySessionStore, IAppUsageHistoryStore
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly object _writeLock = new();

    private readonly Dictionary<string, long> _gpuKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _fanKeys = new(StringComparer.Ordinal);
    // OrdinalIgnoreCase matches app_series.name's COLLATE NOCASE and
    // ProcessAggregation/ResolveExecutablePath's name comparisons - a
    // process name observed with differing case must resolve to the same
    // app row, not fragment into a second one.
    private readonly Dictionary<string, long> _appKeys = new(StringComparer.OrdinalIgnoreCase);

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
                UpsertScalarRollup(tx, samples);
                UpsertGpuRollup(tx, samples, pendingGpuKeys);
                UpsertFanRollup(tx, samples, pendingFanKeys);
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

    // The rollup is minute-aligned, so it can only serve a step that is a
    // whole multiple of a minute - every MetricsHistory.StepLadderSeconds
    // rung at or above that width already is, so MonitoringHistoryRoutes'
    // DecimatedPathMinStepSeconds means the *FromRaw variants below are
    // unreached from the real ladder and stay only as a defensive fallback
    // for a non-ladder step, exercised directly by DecimatedHistoryStoreTests
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
                   AVG(cpu_temp_x10) / 10.0, MAX(cpu_temp_x10) / 10.0
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
                NullableDouble(reader, 9), NullableDouble(reader, 10)));
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
                   SUM(cpu_temp_sum_x10) * 1.0 / SUM(cpu_temp_cnt) / 10.0, MAX(cpu_temp_max_x10) / 10.0
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
                NullableDouble(reader, 9), NullableDouble(reader, 10)));
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

    // One upsert per (minute, entity) touched by this flush's samples - at
    // most MetricsHistory.FlushSeconds/60 rounded up per table, never one
    // row per raw second. sum/cnt (not a running avg) is the accumulated
    // shape so a later flush landing in the same minute can add its own
    // batch's contribution without needing to unmerge a prior average;
    // avg is only ever computed at read time as sum/cnt. cnt gates the
    // read-time division (SQLite divide-by-zero yields NULL), so a sum
    // left at a phantom 0 by COALESCE when both the existing row and this
    // batch had no data for a field never surfaces - only cnt has to be
    // trustworthy, and it always is (never itself COALESCEd).
    private sealed class ScalarRollupAccumulator
    {
        public double? CpuSum; public int CpuCnt; public double? CpuMax;
        public double? MemSum; public int MemCnt; public double? MemMax;
        public double? NetInSum; public int NetInCnt; public double? NetInMax;
        public double? NetOutSum; public int NetOutCnt; public double? NetOutMax;
        public double? TempSum; public int TempCnt; public double? TempMax;

        public void Add(MetricSample s)
        {
            AddField(s.CpuPercent, ref CpuSum, ref CpuCnt, ref CpuMax);
            AddField(s.MemoryPercent, ref MemSum, ref MemCnt, ref MemMax);
            AddField(s.NetInBytesPerSec, ref NetInSum, ref NetInCnt, ref NetInMax);
            AddField(s.NetOutBytesPerSec, ref NetOutSum, ref NetOutCnt, ref NetOutMax);
            AddField(s.CpuTempC, ref TempSum, ref TempCnt, ref TempMax);
        }
    }

    private sealed class GpuRollupAccumulator
    {
        public double? LoadSum; public int LoadCnt; public double? LoadMax;
        public double? TempSum; public int TempCnt; public double? TempMax;

        public void Add(GpuReading g)
        {
            AddField(g.LoadPercent, ref LoadSum, ref LoadCnt, ref LoadMax);
            AddField(g.TempC, ref TempSum, ref TempCnt, ref TempMax);
        }
    }

    private sealed class FanRollupAccumulator
    {
        public double? RpmSum; public int RpmCnt; public double? RpmMax;
        public double? DutySum; public int DutyCnt; public double? DutyMax;

        public void Add(FanReading f)
        {
            AddField(f.Rpm, ref RpmSum, ref RpmCnt, ref RpmMax);
            AddField(f.Duty, ref DutySum, ref DutyCnt, ref DutyMax);
        }
    }

    private static void AddField(double? value, ref double? sum, ref int cnt, ref double? max)
    {
        if (value is not { } v)
        {
            return;
        }
        sum = (sum ?? 0) + v;
        cnt++;
        max = max is { } m ? Math.Max(m, v) : v;
    }

    private static void AddField(int? value, ref double? sum, ref int cnt, ref double? max) =>
        AddField((double?)value, ref sum, ref cnt, ref max);

    private static long ResolveKnownKey(string id, Dictionary<string, long> cache, Dictionary<string, long> pendingKeys) =>
        cache.TryGetValue(id, out var key) ? key : pendingKeys[id];

    private void UpsertScalarRollup(SqliteTransaction tx, IReadOnlyList<MetricSample> samples)
    {
        var byMinute = new Dictionary<long, ScalarRollupAccumulator>();
        foreach (var s in samples)
        {
            var minute = s.TsSec / 60 * 60;
            if (!byMinute.TryGetValue(minute, out var acc))
            {
                acc = new ScalarRollupAccumulator();
                byMinute[minute] = acc;
            }
            acc.Add(s);
        }

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO metric_minutes (
                ts_min, cpu_sum_x10, cpu_cnt, cpu_max_x10,
                mem_sum_x10, mem_cnt, mem_max_x10,
                net_in_sum, net_in_cnt, net_in_max,
                net_out_sum, net_out_cnt, net_out_max,
                cpu_temp_sum_x10, cpu_temp_cnt, cpu_temp_max_x10)
            VALUES ($ts, $cpuSum, $cpuCnt, $cpuMax, $memSum, $memCnt, $memMax,
                    $netInSum, $netInCnt, $netInMax, $netOutSum, $netOutCnt, $netOutMax,
                    $tempSum, $tempCnt, $tempMax)
            ON CONFLICT(ts_min) DO UPDATE SET
                cpu_sum_x10 = COALESCE(cpu_sum_x10, 0) + COALESCE(excluded.cpu_sum_x10, 0),
                cpu_cnt = cpu_cnt + excluded.cpu_cnt,
                cpu_max_x10 = MAX(COALESCE(cpu_max_x10, excluded.cpu_max_x10), COALESCE(excluded.cpu_max_x10, cpu_max_x10)),
                mem_sum_x10 = COALESCE(mem_sum_x10, 0) + COALESCE(excluded.mem_sum_x10, 0),
                mem_cnt = mem_cnt + excluded.mem_cnt,
                mem_max_x10 = MAX(COALESCE(mem_max_x10, excluded.mem_max_x10), COALESCE(excluded.mem_max_x10, mem_max_x10)),
                net_in_sum = COALESCE(net_in_sum, 0) + COALESCE(excluded.net_in_sum, 0),
                net_in_cnt = net_in_cnt + excluded.net_in_cnt,
                net_in_max = MAX(COALESCE(net_in_max, excluded.net_in_max), COALESCE(excluded.net_in_max, net_in_max)),
                net_out_sum = COALESCE(net_out_sum, 0) + COALESCE(excluded.net_out_sum, 0),
                net_out_cnt = net_out_cnt + excluded.net_out_cnt,
                net_out_max = MAX(COALESCE(net_out_max, excluded.net_out_max), COALESCE(excluded.net_out_max, net_out_max)),
                cpu_temp_sum_x10 = COALESCE(cpu_temp_sum_x10, 0) + COALESCE(excluded.cpu_temp_sum_x10, 0),
                cpu_temp_cnt = cpu_temp_cnt + excluded.cpu_temp_cnt,
                cpu_temp_max_x10 = MAX(COALESCE(cpu_temp_max_x10, excluded.cpu_temp_max_x10), COALESCE(excluded.cpu_temp_max_x10, cpu_temp_max_x10));
        """;
        var pTs = AddParam(cmd, "$ts");
        var pCpuSum = AddParam(cmd, "$cpuSum");
        var pCpuCnt = AddParam(cmd, "$cpuCnt");
        var pCpuMax = AddParam(cmd, "$cpuMax");
        var pMemSum = AddParam(cmd, "$memSum");
        var pMemCnt = AddParam(cmd, "$memCnt");
        var pMemMax = AddParam(cmd, "$memMax");
        var pNetInSum = AddParam(cmd, "$netInSum");
        var pNetInCnt = AddParam(cmd, "$netInCnt");
        var pNetInMax = AddParam(cmd, "$netInMax");
        var pNetOutSum = AddParam(cmd, "$netOutSum");
        var pNetOutCnt = AddParam(cmd, "$netOutCnt");
        var pNetOutMax = AddParam(cmd, "$netOutMax");
        var pTempSum = AddParam(cmd, "$tempSum");
        var pTempCnt = AddParam(cmd, "$tempCnt");
        var pTempMax = AddParam(cmd, "$tempMax");

        foreach (var (minute, acc) in byMinute)
        {
            pTs.Value = minute;
            pCpuSum.Value = ScaleX10(acc.CpuSum);
            pCpuCnt.Value = acc.CpuCnt;
            pCpuMax.Value = ScaleX10(acc.CpuMax);
            pMemSum.Value = ScaleX10(acc.MemSum);
            pMemCnt.Value = acc.MemCnt;
            pMemMax.Value = ScaleX10(acc.MemMax);
            pNetInSum.Value = ScaleWhole(acc.NetInSum);
            pNetInCnt.Value = acc.NetInCnt;
            pNetInMax.Value = ScaleWhole(acc.NetInMax);
            pNetOutSum.Value = ScaleWhole(acc.NetOutSum);
            pNetOutCnt.Value = acc.NetOutCnt;
            pNetOutMax.Value = ScaleWhole(acc.NetOutMax);
            pTempSum.Value = ScaleX10(acc.TempSum);
            pTempCnt.Value = acc.TempCnt;
            pTempMax.Value = ScaleX10(acc.TempMax);
            cmd.ExecuteNonQuery();
        }
    }

    private void UpsertGpuRollup(SqliteTransaction tx, IReadOnlyList<MetricSample> samples, Dictionary<string, long> pendingGpuKeys)
    {
        var byMinuteGpu = new Dictionary<(long Minute, long GpuKey), GpuRollupAccumulator>();
        foreach (var s in samples)
        {
            var minute = s.TsSec / 60 * 60;
            foreach (var g in s.Gpus)
            {
                var mapKey = (minute, ResolveKnownKey(g.GpuId, _gpuKeys, pendingGpuKeys));
                if (!byMinuteGpu.TryGetValue(mapKey, out var acc))
                {
                    acc = new GpuRollupAccumulator();
                    byMinuteGpu[mapKey] = acc;
                }
                acc.Add(g);
            }
        }
        if (byMinuteGpu.Count == 0)
        {
            return;
        }

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO gpu_minutes (ts_min, gpu, load_sum_x10, load_cnt, load_max_x10, temp_sum_x10, temp_cnt, temp_max_x10)
            VALUES ($ts, $gpu, $loadSum, $loadCnt, $loadMax, $tempSum, $tempCnt, $tempMax)
            ON CONFLICT(ts_min, gpu) DO UPDATE SET
                load_sum_x10 = COALESCE(load_sum_x10, 0) + COALESCE(excluded.load_sum_x10, 0),
                load_cnt = load_cnt + excluded.load_cnt,
                load_max_x10 = MAX(COALESCE(load_max_x10, excluded.load_max_x10), COALESCE(excluded.load_max_x10, load_max_x10)),
                temp_sum_x10 = COALESCE(temp_sum_x10, 0) + COALESCE(excluded.temp_sum_x10, 0),
                temp_cnt = temp_cnt + excluded.temp_cnt,
                temp_max_x10 = MAX(COALESCE(temp_max_x10, excluded.temp_max_x10), COALESCE(excluded.temp_max_x10, temp_max_x10));
        """;
        var pTs = AddParam(cmd, "$ts");
        var pGpu = AddParam(cmd, "$gpu");
        var pLoadSum = AddParam(cmd, "$loadSum");
        var pLoadCnt = AddParam(cmd, "$loadCnt");
        var pLoadMax = AddParam(cmd, "$loadMax");
        var pTempSum = AddParam(cmd, "$tempSum");
        var pTempCnt = AddParam(cmd, "$tempCnt");
        var pTempMax = AddParam(cmd, "$tempMax");

        foreach (var ((minute, gpuKey), acc) in byMinuteGpu)
        {
            pTs.Value = minute;
            pGpu.Value = gpuKey;
            pLoadSum.Value = ScaleX10(acc.LoadSum);
            pLoadCnt.Value = acc.LoadCnt;
            pLoadMax.Value = ScaleX10(acc.LoadMax);
            pTempSum.Value = ScaleX10(acc.TempSum);
            pTempCnt.Value = acc.TempCnt;
            pTempMax.Value = ScaleX10(acc.TempMax);
            cmd.ExecuteNonQuery();
        }
    }

    private void UpsertFanRollup(SqliteTransaction tx, IReadOnlyList<MetricSample> samples, Dictionary<string, long> pendingFanKeys)
    {
        var byMinuteFan = new Dictionary<(long Minute, long FanKey), FanRollupAccumulator>();
        foreach (var s in samples)
        {
            var minute = s.TsSec / 60 * 60;
            foreach (var f in s.Fans)
            {
                var mapKey = (minute, ResolveKnownKey(f.FanId, _fanKeys, pendingFanKeys));
                if (!byMinuteFan.TryGetValue(mapKey, out var acc))
                {
                    acc = new FanRollupAccumulator();
                    byMinuteFan[mapKey] = acc;
                }
                acc.Add(f);
            }
        }
        if (byMinuteFan.Count == 0)
        {
            return;
        }

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO fan_minutes (ts_min, fan, rpm_sum, rpm_cnt, rpm_max, duty_sum, duty_cnt, duty_max)
            VALUES ($ts, $fan, $rpmSum, $rpmCnt, $rpmMax, $dutySum, $dutyCnt, $dutyMax)
            ON CONFLICT(ts_min, fan) DO UPDATE SET
                rpm_sum = COALESCE(rpm_sum, 0) + COALESCE(excluded.rpm_sum, 0),
                rpm_cnt = rpm_cnt + excluded.rpm_cnt,
                rpm_max = MAX(COALESCE(rpm_max, excluded.rpm_max), COALESCE(excluded.rpm_max, rpm_max)),
                duty_sum = COALESCE(duty_sum, 0) + COALESCE(excluded.duty_sum, 0),
                duty_cnt = duty_cnt + excluded.duty_cnt,
                duty_max = MAX(COALESCE(duty_max, excluded.duty_max), COALESCE(excluded.duty_max, duty_max));
        """;
        var pTs = AddParam(cmd, "$ts");
        var pFan = AddParam(cmd, "$fan");
        var pRpmSum = AddParam(cmd, "$rpmSum");
        var pRpmCnt = AddParam(cmd, "$rpmCnt");
        var pRpmMax = AddParam(cmd, "$rpmMax");
        var pDutySum = AddParam(cmd, "$dutySum");
        var pDutyCnt = AddParam(cmd, "$dutyCnt");
        var pDutyMax = AddParam(cmd, "$dutyMax");

        foreach (var ((minute, fanKey), acc) in byMinuteFan)
        {
            pTs.Value = minute;
            pFan.Value = fanKey;
            pRpmSum.Value = ScaleWhole(acc.RpmSum);
            pRpmCnt.Value = acc.RpmCnt;
            pRpmMax.Value = ScaleWhole(acc.RpmMax);
            pDutySum.Value = ScaleWhole(acc.DutySum);
            pDutyCnt.Value = acc.DutyCnt;
            pDutyMax.Value = ScaleWhole(acc.DutyMax);
            cmd.ExecuteNonQuery();
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

        foreach (var (table, tsColumn) in RollupTables)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE {tsColumn} < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoffSec);
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

            CREATE TABLE IF NOT EXISTS metric_minutes (
                ts_min           INTEGER PRIMARY KEY,
                cpu_sum_x10      INTEGER, cpu_cnt INTEGER NOT NULL DEFAULT 0, cpu_max_x10 INTEGER,
                mem_sum_x10      INTEGER, mem_cnt INTEGER NOT NULL DEFAULT 0, mem_max_x10 INTEGER,
                net_in_sum       INTEGER, net_in_cnt INTEGER NOT NULL DEFAULT 0, net_in_max INTEGER,
                net_out_sum      INTEGER, net_out_cnt INTEGER NOT NULL DEFAULT 0, net_out_max INTEGER,
                cpu_temp_sum_x10 INTEGER, cpu_temp_cnt INTEGER NOT NULL DEFAULT 0, cpu_temp_max_x10 INTEGER
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
            }
        }
    }

    private static readonly string[] AppSecondsTables = { "app_cpu_seconds", "app_mem_seconds", "app_gpu_seconds" };

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
                  AND key NOT IN (SELECT app FROM app_gpu_seconds);
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

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT se.name, SUM(t.value_x10), MAX(t.value_x10) / 10.0
                FROM {table} t
                JOIN app_series se ON se.key = t.app
                WHERE {gpuFilter}t.ts BETWEEN $from AND $to
                GROUP BY t.app
                ORDER BY SUM(t.value_x10) DESC
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
                result.Add(new AppWindowStat(reader.GetString(0), sum / (expectedTicks * 10.0), reader.GetDouble(2)));
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
            var gpuFilter = gpuKey is not null ? "gpu = $gpu AND " : "";
            var vramSelect = isGpu ? ", vram_mb" : "";

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT ts, value_x10{vramSelect}
                FROM {table}
                WHERE {gpuFilter}app = $app AND ts BETWEEN $from AND $to
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
            // NULL itself when the app is in none of the three tables.
            cmd.CommandText = """
                SELECT MIN(ts) FROM (
                    SELECT MIN(ts) AS ts FROM app_cpu_seconds WHERE app = $app
                    UNION ALL
                    SELECT MIN(ts) FROM app_mem_seconds WHERE app = $app
                    UNION ALL
                    SELECT MIN(ts) FROM app_gpu_seconds WHERE app = $app
                );
            """;
            cmd.Parameters.AddWithValue("$app", appKey);
            var result = cmd.ExecuteScalar();
            return result is null or DBNull ? null : Convert.ToInt64(result);
        }
    }

    // "cpu"/"memory" resolve directly; "gpu:<gid>" resolves through the
    // existing gpu_series cache so app rows and scalar gpu rows always agree
    // on which surrogate key a gid maps to. A gid never seen by the scalar
    // path (no gpu_series row) yields a null table - there is nothing to
    // query, not an error.
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
        if (metric.StartsWith("gpu:", StringComparison.Ordinal))
        {
            var gid = metric[4..];
            return _gpuKeys.TryGetValue(gid, out var key) ? ("app_gpu_seconds", key) : (null, null);
        }
        return (null, null);
    }

    private static string ResolveDatabasePath() =>
        Path.Combine(NexusDataPaths.DatabaseDir(), "metrics.db");
}
