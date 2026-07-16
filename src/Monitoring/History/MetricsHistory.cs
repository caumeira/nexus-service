using System.Collections.Generic;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Shared constants and id conventions for the metrics history pipeline
/// (MetricsSampler -> MetricsSampleBuffer -> IMetricsHistoryStore -> GET
/// /monitoring/history), so the sampler, store, and route agree on retention,
/// flush cadence, and the decimation step ladder without re-deriving them.
/// </summary>
public static class MetricsHistory
{
    /// <summary>Days of 1Hz metric_seconds/gpu_seconds/fan_seconds rows kept
    /// in metrics.db before pruning.</summary>
    public const int RetentionDays = 7;

    /// <summary>Ticks (seconds, MetricsSampler runs at 1Hz) between store
    /// flushes of the buffered tail.</summary>
    public const int FlushSeconds = 30;

    /// <summary>Decimation step widths in seconds, narrowest first. A query
    /// picks the first step wide enough to keep its point count under the
    /// requested maxPoints (see MetricsDecimation.StepSecondsFor).</summary>
    public static readonly IReadOnlyList<int> StepLadderSeconds =
        new[] { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };

    /// <summary>Sanitizes an LHM-style hardware identifier (e.g.
    /// "/gpu-nvidia/0") into a wire-safe series id fragment ("gpu-nvidia-0"):
    /// strips one leading slash, then replaces any remaining slash with a
    /// hyphen. Applied identically to GPU and fan channel ids so
    /// "gpu:&lt;gid&gt;"/"gpu-temp:&lt;gid&gt;" and
    /// "fan:&lt;fid&gt;"/"fan-duty:&lt;fid&gt;" ids round-trip the same way on
    /// both the write and read paths.</summary>
    public static string SanitizeId(string rawId)
    {
        if (string.IsNullOrEmpty(rawId))
        {
            return rawId;
        }
        var trimmed = rawId[0] == '/' ? rawId[1..] : rawId;
        return trimmed.Replace('/', '-');
    }
}
