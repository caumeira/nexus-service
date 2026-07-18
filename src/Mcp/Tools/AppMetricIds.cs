using System;
using System.Linq;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Validates the app-usage metric ids get_top_apps/query_app_history
/// accept, matching AppUsageStore.ResolveMetric's accepted set (that method
/// is private, so this is a deliberate, narrow duplication of just the id
/// shapes, not the query logic).</summary>
internal static class AppMetricIds
{
    internal const string ValidMetricsText =
        "cpu, memory, gpu, vram, storage, storage-read, storage-write, net, net-down, net-up " +
        "(gpu and vram accept a :<gpuId> suffix for one adapter)";

    private static readonly string[] BareMetrics =
    {
        "cpu", "memory", "storage", "storage-read", "storage-write", "net", "net-down", "net-up", "gpu", "vram",
    };

    internal static bool IsValid(string metric) =>
        BareMetrics.Contains(metric, StringComparer.Ordinal)
        || (metric.StartsWith("gpu:", StringComparison.Ordinal) && metric.Length > 4)
        || (metric.StartsWith("vram:", StringComparison.Ordinal) && metric.Length > 5);
}
