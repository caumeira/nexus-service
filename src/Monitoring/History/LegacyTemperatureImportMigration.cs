using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Nexus.Service.Persistence;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// One-time import of the retired temperature.db's temp_buckets rows into
/// metrics.db's own temp_buckets table. Run once by SqliteMetricsHistoryStore
/// (schema_meta version gate) on the first boot after the unified
/// temperature pipeline lands; never deletes temperature.db afterward, so a
/// failed or partial import always has the source data to retry against.
///
/// Both tables share the same bucket width (MetricsHistory.TempBucketMinutes
/// matches the retired temperature.db pipeline's own bucket width), so every
/// row maps one-to-one with no resampling across bucket boundaries -
/// bucket_utc (ms) becomes bucket_ts (seconds), avg_c/max_c convert to the
/// x10 fixed-point columns. sum_x10 has no legacy source column (the old
/// schema stored only the average, not a raw sum) and is reconstructed as
/// avg_c * 10 * samples, which round-trips back to the same avg_c on read
/// but is not a byte-identical copy of anything that existed before.
///
/// GPU rows import under their OLD id (NVML UUID-based, e.g. "gpu:GPU-abc...")
/// verbatim - this store's live GPU temp now keys off a different,
/// LHM-sanitized id (SystemMetricsSource -> MetricsHistory.SanitizeId, e.g.
/// "gpu:gpu-nvidia-0"), matching what gpu_seconds already used for load. A
/// GPU with prior history therefore renders as two chart series for the
/// remainder of the 90-day retention window: the imported one under the old
/// id, and a new one accumulating under the new id from this boot onward.
/// No id-remapping is attempted - the old GpuComponentIdMigration precedent
/// re-keyed by matching GPU name, but that requires a live GPU snapshot at
/// migration time and this migration runs before the sampler has taken one.
/// The imported series simply ages out after 90 days like any other row.
/// </summary>
internal static class LegacyTemperatureImportMigration
{
    public static void Run(SqliteConnection metricsConnection, string legacyDbPath)
    {
        var rows = ReadLegacyBuckets(legacyDbPath);
        if (rows.Count == 0)
        {
            return;
        }

        using var tx = metricsConnection.BeginTransaction();
        using var cmd = metricsConnection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO temp_buckets (bucket_ts, component_id, kind, name, sum_x10, cnt, max_x10)
            VALUES ($ts, $id, $kind, $name, $sum, $cnt, $max);
        """;
        var pTs = AddParam(cmd, "$ts");
        var pId = AddParam(cmd, "$id");
        var pKind = AddParam(cmd, "$kind");
        var pName = AddParam(cmd, "$name");
        var pSum = AddParam(cmd, "$sum");
        var pCnt = AddParam(cmd, "$cnt");
        var pMax = AddParam(cmd, "$max");

        foreach (var row in rows)
        {
            pTs.Value = row.BucketUtcMs / 1000;
            pId.Value = row.ComponentId;
            pKind.Value = row.Kind;
            pName.Value = row.Name;
            pSum.Value = (long)Math.Round(row.AvgC * 10) * row.Samples;
            pCnt.Value = row.Samples;
            pMax.Value = (long)Math.Round(row.MaxC * 10);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();

        Console.WriteLine($"[metrics-history-store] imported {rows.Count} temp_buckets rows from {legacyDbPath}");
    }

    internal readonly record struct LegacyBucket(
        string ComponentId, string Kind, string Name, long BucketUtcMs, double AvgC, double MaxC, int Samples);

    private static List<LegacyBucket> ReadLegacyBuckets(string legacyDbPath)
    {
        var result = new List<LegacyBucket>();
        using var connection = SqliteStores.OpenConnection(legacyDbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT component_id, kind, name, bucket_utc, avg_c, max_c, samples FROM temp_buckets;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new LegacyBucket(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetDouble(4), reader.GetDouble(5), reader.GetInt32(6)));
        }
        return result;
    }

    private static SqliteParameter AddParam(SqliteCommand cmd, string name)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }
}
