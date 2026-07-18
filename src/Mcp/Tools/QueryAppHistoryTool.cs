using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only history tool: one app's usage series for one metric over
/// a window.</summary>
public sealed class QueryAppHistoryTool : IMcpTool
{
    internal const int DefaultMaxPoints = 200;
    internal const int MaxAllowedPoints = 2000;
    private const int MaxAllowedMinutes = MetricsHistory.RetentionDays * 24 * 60;

    private readonly IAppUsageHistoryStore _apps;

    public QueryAppHistoryTool(IAppUsageHistoryStore apps) => _apps = apps;

    public string Name => "query_app_history";
    public string Title => "App History";

    public string Description =>
        $"Returns a time series for one app's usage under one metric ({AppMetricIds.ValidMetricsText}) " +
        "over a window, in minutes. Call get_top_apps first to find the app's exact name.";

    public McpCapability Capability => McpCapability.History;
    public bool ReadOnly => true;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"metric\":{\"type\":\"string\",\"description\":\"" + AppMetricIds.ValidMetricsText + ".\"}," +
        "\"app\":{\"type\":\"string\",\"description\":\"App name from get_top_apps.\"}," +
        "\"minutes\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"How far back to look, in minutes.\"}," +
        "\"maxPoints\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"Maximum points to return; thinned evenly when the window has more.\"}" +
        "},\"required\":[\"metric\",\"app\",\"minutes\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var metric = McpArgs.StringArg(args, "metric");
        if (string.IsNullOrEmpty(metric))
        {
            return Task.FromResult(McpToolExecutionResult.Error("'metric' is required."));
        }
        if (!AppMetricIds.IsValid(metric))
        {
            return Task.FromResult(McpToolExecutionResult.Error($"'metric' must be one of: {AppMetricIds.ValidMetricsText}."));
        }

        var app = McpArgs.StringArg(args, "app");
        if (string.IsNullOrEmpty(app))
        {
            return Task.FromResult(McpToolExecutionResult.Error("'app' is required."));
        }

        if (McpArgs.IntArg(args, "minutes") is not { } minutes || minutes < 1)
        {
            return Task.FromResult(McpToolExecutionResult.Error("'minutes' is required and must be a positive integer."));
        }
        minutes = Math.Min(minutes, MaxAllowedMinutes);

        var maxPoints = McpArgs.IntArg(args, "maxPoints") is { } requested && requested > 0
            ? Math.Min(requested, MaxAllowedPoints)
            : DefaultMaxPoints;

        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fromSec = nowSec - minutes * 60L;

        var raw = _apps.QueryAppSeries(metric, app, fromSec, nowSec);
        var isGpuMetric = metric == "gpu" || metric.StartsWith("gpu:", StringComparison.Ordinal)
            || metric == "vram" || metric.StartsWith("vram:", StringComparison.Ordinal);

        double? vramAvgMb = null;
        if (isGpuMetric)
        {
            var vramValues = raw.Where(p => p.VramMb is not null).Select(p => p.VramMb!.Value).ToList();
            if (vramValues.Count > 0)
            {
                vramAvgMb = Math.Round(vramValues.Average(), 1);
            }
        }

        var stepSeconds = MetricsDecimation.StepSecondsFor(Math.Max(0, nowSec - fromSec), maxPoints);
        var points = MetricsDecimation.Decimate(
            raw.Select(p => new MetricSamplePoint(p.TsSec, p.Value)), fromSec, nowSec, stepSeconds);

        var result = new McpAppHistoryResult
        {
            Metric = metric,
            App = app,
            VramAvgMb = vramAvgMb,
            Points = points.Select(p => new McpAppHistoryPoint { T = p.T * 1000, Value = Math.Round(p.Avg, 1) }).ToList(),
        };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpAppHistoryResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
