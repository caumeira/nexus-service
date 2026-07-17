using System;
using System.Collections.Generic;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Persistent store for 1Hz system metrics. MetricsSampler is the only
/// writer (one Append per flush, every MetricsHistory.FlushSeconds ticks);
/// GET /monitoring/history is the reader, merging Query results against
/// MetricsSampleBuffer's unflushed tail.
/// </summary>
public interface IMetricsHistoryStore : IDisposable
{
    /// <summary>Batches samples in one transaction (INSERT OR REPLACE keyed
    /// by ts for scalars, by (ts, entity) for gpu/fan readings). When
    /// pruneCutoffSec is not null, also deletes every row older than it,
    /// inside that same transaction.</summary>
    void Append(IReadOnlyList<MetricSample> samples, long? pruneCutoffSec);

    /// <summary>Reconstructs samples with ts in [fromSec, toSec], ascending
    /// by ts.</summary>
    IReadOnlyList<MetricSample> Query(long fromSec, long toSec);

    /// <summary>Slot-aggregated (avg/max) scalar fields, one row per slot
    /// that has data (slot = ts/step*step, matching MetricsDecimation).
    /// Aggregation runs in the store (SQL GROUP BY for the SQLite
    /// implementation), avoiding materializing every raw row into C# for a
    /// wide window - see MonitoringHistoryRoutes' route-level use for when
    /// this is worth it over Query + MetricsDecimation.Decimate.</summary>
    IReadOnlyList<ScalarDecimatedSlot> QueryScalarsDecimated(long fromSec, long toSec, int stepSeconds);

    /// <summary>Slot-aggregated per-GPU load/temperature, one row per
    /// (gpu, slot) that has data.</summary>
    IReadOnlyList<GpuDecimatedSlot> QueryGpuDecimated(long fromSec, long toSec, int stepSeconds);

    /// <summary>Slot-aggregated per-fan RPM/duty, one row per (fan, slot)
    /// that has data.</summary>
    IReadOnlyList<FanDecimatedSlot> QueryFanDecimated(long fromSec, long toSec, int stepSeconds);

    /// <summary>One bucket for one temperature component (cpu / gpu:&lt;id&gt;
    /// / storage:&lt;serial&gt; / ram:&lt;id&gt;), bucket-aligned (see
    /// MetricsHistory.TempBucketMinutes), read from the 90-day temp_buckets
    /// rollup - see QueryTemperatureBuckets.</summary>
    IReadOnlyList<TemperatureBucketRow> QueryTemperatureBuckets(long fromUtcMs, long toUtcMs);
}

/// <summary>One bucket for one temperature component. BucketUtcMs is the
/// bucket's start, aligned to MetricsHistory.TempBucketMinutes;
/// AvgC/MaxC/Samples are derived from every 1Hz reading folded into that
/// bucket. Shared by TemperatureInsights (decimation/episode analysis) and
/// the /diagnostics/temperatures route.</summary>
public sealed record TemperatureBucketRow(
    string ComponentId, string Kind, string Name, long BucketUtcMs,
    double AvgC, double MaxC, int Samples);

/// <summary>One slot's avg/max for every scalar field. A field is null only
/// when every raw reading in the slot was itself null (source failed that
/// whole slot), matching MetricSample's "null = source failed" convention.</summary>
public readonly record struct ScalarDecimatedSlot(
    long Slot,
    double? CpuAvg, double? CpuMax,
    double? MemAvg, double? MemMax,
    double? NetInAvg, double? NetInMax,
    double? NetOutAvg, double? NetOutMax,
    double? CpuTempAvg, double? CpuTempMax);

/// <summary>One GPU's slot-aggregated load/temperature.</summary>
public readonly record struct GpuDecimatedSlot(
    string GpuId, string Name, long Slot,
    double? LoadAvg, double? LoadMax, double? TempAvg, double? TempMax);

/// <summary>One fan channel's slot-aggregated RPM/duty.</summary>
public readonly record struct FanDecimatedSlot(
    string FanId, string Name, long Slot,
    double? RpmAvg, double? RpmMax, double? DutyAvg, double? DutyMax);
