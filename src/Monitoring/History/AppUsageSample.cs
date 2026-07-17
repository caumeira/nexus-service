using System.Collections.Generic;

namespace Nexus.Service.Monitoring.History;

/// <summary>One app's reading within an AppMetricSample. Name is the
/// process-name aggregate key (matches ProcessMonitor's grouping); VramMb is
/// only populated for gpu metrics, null otherwise.</summary>
public sealed record AppUsagePoint(string Name, double Value, double? VramMb);

/// <summary>Every app's reading for one metric at one sampling tick. Metric
/// is a wire-shaped series id: "cpu", "memory", "gpu:&lt;gid&gt;", or
/// "vram:&lt;gid&gt;" (gid already MetricsHistory.SanitizeId-ed, matching the
/// scalar gpu series convention so a metric id round-trips between the
/// scalar and per-app tables). Apps is already trimmed to the top-N by
/// value.</summary>
public sealed record AppMetricSample(string Metric, IReadOnlyList<AppUsagePoint> Apps);

/// <summary>One per-app sampling tick: every metric's top-N apps, gathered
/// on MetricsHistory.AppSampleIntervalSeconds sub-cadence inside
/// MetricsSampler's 1Hz loop.</summary>
public sealed record AppUsageTick(long TsSec, IReadOnlyList<AppMetricSample> Metrics);
