using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Mcp.History;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only history tool: min/max/avg/latest for every recorded sensor
/// over a window - doubles as the sensor id index query_sensor_history's errors point to.</summary>
public sealed class GetHistorySummaryTool : IMcpTool
{
    private const int MaxAllowedMinutes = AiHistoryRetention.FiveMinuteTierRetentionMinutes;

    private readonly IAiHistoryStore _history;

    public GetHistorySummaryTool(IAiHistoryStore history) => _history = history;

    public string Name => "get_history_summary";
    public string Title => "History Summary";

    public string Description =>
        "Returns min/max/average/latest for every recorded sensor (CPU/GPU temperature, load, power, " +
        "fan RPMs, pump/coolant where present) over a window, in minutes. Call this first to see what " +
        "sensor ids are available and how they have been trending, before drilling into one with " +
        "query_sensor_history.";

    public McpCapability Capability => McpCapability.History;
    public bool ReadOnly => true;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"minutes\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"How far back to summarize, in minutes.\"}" +
        "},\"required\":[\"minutes\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        if (!_history.IsAvailable)
        {
            return Task.FromResult(McpToolExecutionResult.Error(HistoryToolText.Unavailable));
        }

        if (McpArgs.IntArg(args, "minutes") is not { } minutes || minutes < 1)
        {
            return Task.FromResult(McpToolExecutionResult.Error("'minutes' is required and must be a positive integer."));
        }
        minutes = Math.Min(minutes, MaxAllowedMinutes);

        var nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fromUtcMs = nowUtcMs - minutes * 60_000L;

        var rows = _history.Summarize(fromUtcMs, nowUtcMs);
        var result = new McpHistorySummaryResult
        {
            Minutes = minutes,
            Sensors = rows.Select(r => new McpSensorSummary
            {
                SensorId = r.SensorId,
                Name = r.Name,
                Unit = r.Unit,
                Min = r.Min,
                Max = r.Max,
                Avg = r.Avg,
                Latest = r.Latest,
                LatestAtUtc = r.LatestAtUtcMs,
                Samples = r.Samples,
            }).ToList(),
        };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpHistorySummaryResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
