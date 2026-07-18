using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only history tool: apps ranked by usage for one metric over a
/// window.</summary>
public sealed class GetTopAppsTool : IMcpTool
{
    internal const int DefaultLimit = 10;
    internal const int MaxAllowedLimit = 100;
    private const int MaxAllowedMinutes = MetricsHistory.RetentionDays * 24 * 60;

    private readonly IAppUsageHistoryStore _apps;

    public GetTopAppsTool(IAppUsageHistoryStore apps) => _apps = apps;

    public string Name => "get_top_apps";
    public string Title => "Top Apps";

    public string Description =>
        $"Returns the top apps ranked by average usage for one metric ({AppMetricIds.ValidMetricsText}) " +
        "over a window, in minutes. System-wide disk and network totals are available through " +
        "query_sensor_history (disk.read, disk.write, net.in, net.out) instead of this tool.";

    public McpCapability Capability => McpCapability.History;
    public bool ReadOnly => true;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"metric\":{\"type\":\"string\",\"description\":\"" + AppMetricIds.ValidMetricsText + ".\"}," +
        "\"minutes\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"How far back to look, in minutes.\"}," +
        "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"Maximum apps to return, ranked by average usage.\"}" +
        "},\"required\":[\"metric\",\"minutes\"],\"additionalProperties\":false}";

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

        if (McpArgs.IntArg(args, "minutes") is not { } minutes || minutes < 1)
        {
            return Task.FromResult(McpToolExecutionResult.Error("'minutes' is required and must be a positive integer."));
        }
        minutes = Math.Min(minutes, MaxAllowedMinutes);

        var limit = McpArgs.IntArg(args, "limit") is { } requested && requested > 0
            ? Math.Min(requested, MaxAllowedLimit)
            : DefaultLimit;

        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fromSec = nowSec - minutes * 60L;

        var top = _apps.QueryTopApps(metric, fromSec, nowSec, limit);
        var result = new McpTopAppsResult
        {
            Metric = metric,
            Minutes = minutes,
            Apps = top.Select(a => new McpTopApp { Name = a.Name, Avg = Math.Round(a.Avg, 1), Max = Math.Round(a.Max, 1) }).ToList(),
        };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpTopAppsResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
