using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Nexus.Service.Persistence;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// One-time import of the retired temperature.db's 5-minute bucket rows into
/// metrics.db's temp_minutes rollup. Run once by SqliteMetricsHistoryStore
/// (schema_meta version gate) on the first boot after the unified
/// temperature pipeline lands; never deletes temperature.db afterward, so a
/// failed or partial import always has the source data to retry against.
///
/// Each legacy 5-minute bucket becomes up to
/// <see cref="SubBucketsPerLegacyBucket"/> native 1-minute temp_minutes
/// rows, all carrying the bucket's own avg/max (no finer resolution exists
/// in the source) with its sample count split across them. Expanding to the
/// native bucket width - rather than keeping one row per 5 minutes - is what
/// lets TemperatureInsights.DetectEpisodes use a single hardcoded 1-minute
/// contiguity check across both imported and natively-sampled rows; a
/// straight carry-over at the old width would silently stop detecting
/// sustained-high episodes anywhere in the imported history.
/// </summary>
internal static class LegacyTemperatureImportMigration
{
    private const long LegacyBucketMs = 5 * 60_000L;
    private const long NativeBucketMs = 60_000L;
    private const int SubBucketsPerLegacyBucket = (int)(LegacyBucketMs / NativeBucketMs);

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
            INSERT OR REPLACE INTO temp_minutes (ts_min, component_id, kind, name, sum_x10, cnt, max_x10)
            VALUES ($ts, $id, $kind, $name, $sum, $cnt, $max);
        """;
        var pTs = AddParam(cmd, "$ts");
        var pId = AddParam(cmd, "$id");
        var pKind = AddParam(cmd, "$kind");
        var pName = AddParam(cmd, "$name");
        var pSum = AddParam(cmd, "$sum");
        var pCnt = AddParam(cmd, "$cnt");
        var pMax = AddParam(cmd, "$max");

        var imported = 0;
        foreach (var row in rows)
        {
            var counts = DistributeSamples(row.Samples, SubBucketsPerLegacyBucket);
            var avgX10 = (long)Math.Round(row.AvgC * 10);
            var maxX10 = (long)Math.Round(row.MaxC * 10);
            var bucketStartSec = row.BucketUtcMs / 1000;

            for (var i = 0; i < counts.Length; i++)
            {
                if (counts[i] <= 0)
                {
                    continue;
                }
                pTs.Value = bucketStartSec + i * (NativeBucketMs / 1000);
                pId.Value = row.ComponentId;
                pKind.Value = row.Kind;
                pName.Value = row.Name;
                pSum.Value = avgX10 * counts[i];
                pCnt.Value = counts[i];
                pMax.Value = maxX10;
                cmd.ExecuteNonQuery();
                imported++;
            }
        }
        tx.Commit();

        Console.WriteLine($"[metrics-history-store] imported {imported} temp_minutes rows from {legacyDbPath}");
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

    /// <summary>Splits total as evenly as possible across slots, front-loading
    /// the remainder; a slot that would receive 0 is left at 0 rather than
    /// fabricating a reading.</summary>
    internal static int[] DistributeSamples(int total, int slots)
    {
        var result = new int[slots];
        var baseCount = total / slots;
        var remainder = total % slots;
        for (var i = 0; i < slots; i++)
        {
            result[i] = baseCount + (i < remainder ? 1 : 0);
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
