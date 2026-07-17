using System.Collections.Generic;

namespace Nexus.Service.Monitoring.History;

/// <summary>One app's window-aggregate stat for a metric: SQL-side AVG/MAX
/// over the requested [from, to] range, already limited and ordered by the
/// store (top apps by Avg descending).</summary>
public sealed record AppWindowStat(string Name, double Avg, double Max);

/// <summary>One raw (ts, value) reading for a single app within a metric,
/// feeding MetricsDecimation for that app's sparkline points. VramMb is
/// populated only for gpu metrics.</summary>
public sealed record AppRawPoint(long TsSec, double? Value, double? VramMb);

/// <summary>
/// Persistent store for per-app usage history. MetricsSampler is the only
/// writer (one Append per flush, every MetricsHistory.FlushSeconds ticks,
/// carrying every AppSampleBuffer tick since the last flush); GET
/// /monitoring/history/apps is the reader.
///
/// Metric ids match the scalar wire convention: "cpu", "memory",
/// "gpu:&lt;gid&gt;" ("net" is never written - see MonitoringHistoryRoutes
/// for the per-app network attribution decision).
/// </summary>
public interface IAppUsageHistoryStore
{
    /// <summary>Batches every metric sample across every tick in one
    /// transaction. When pruneCutoffSec is not null, also deletes every row
    /// older than it, inside that same transaction.</summary>
    void Append(IReadOnlyList<AppUsageTick> ticks, long? pruneCutoffSec);

    /// <summary>Top maxApps apps by window average for metric, descending.
    /// Aggregation runs in SQL (GROUP BY), not in the caller.</summary>
    IReadOnlyList<AppWindowStat> QueryTopApps(string metric, long fromSec, long toSec, int maxApps);

    /// <summary>Raw (ts, value, vramMb) rows for one app within metric,
    /// ascending by ts - the input MetricsDecimation.Decimate expects.</summary>
    IReadOnlyList<AppRawPoint> QueryAppSeries(string metric, string appName, long fromSec, long toSec);
}
