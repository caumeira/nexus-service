using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Mcp.History;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only history tool: one sensor's series over a time window, tiered
/// by window length and read from the always-on monitoring store.</summary>
public sealed class QuerySensorHistoryTool : IMcpTool
{
    internal const int DefaultMaxPoints = 200;
    internal const int MaxAllowedPoints = 2000;
    private const int MaxAllowedMinutes = MetricsHistory.RetentionDays * 24 * 60;

    private readonly MonitoringSensorHistoryReader _reader;

    public QuerySensorHistoryTool(MonitoringSensorHistoryReader reader) => _reader = reader;

    public string Name => "query_sensor_history";
    public string Title => "Sensor History";

    public string Description =>
        "Returns a time series for one sensor over a window, in minutes, up to " +
        $"{MaxAllowedMinutes / (24 * 60)} days back. Ids are not the same as get_sensors' raw ids - " +
        "call get_history_summary first to find sensor ids from its sensorId field; use this to see " +
        "how a specific sensor has trended, for example whether a temperature has been rising.";

    public McpCapability Capability => McpCapability.History;
    public bool ReadOnly => true;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"sensorId\":{\"type\":\"string\",\"description\":\"Sensor id from get_history_summary.\"}," +
        "\"minutes\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"How far back to look, in minutes.\"}," +
        "\"maxPoints\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"Maximum points to return; thinned evenly when the window has more.\"}" +
        "},\"required\":[\"sensorId\",\"minutes\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var sensorId = McpArgs.StringArg(args, "sensorId");
        if (string.IsNullOrEmpty(sensorId))
        {
            return Task.FromResult(McpToolExecutionResult.Error("'sensorId' is required."));
        }

        if (McpArgs.IntArg(args, "minutes") is not { } minutes || minutes < 1)
        {
            return Task.FromResult(McpToolExecutionResult.Error("'minutes' is required and must be a positive integer."));
        }
        minutes = Math.Min(minutes, MaxAllowedMinutes);

        var maxPoints = McpArgs.IntArg(args, "maxPoints") is { } requested && requested > 0
            ? Math.Min(requested, MaxAllowedPoints)
            : DefaultMaxPoints;

        var nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fromUtcMs = nowUtcMs - minutes * 60_000L;

        var series = _reader.QuerySensorHistory(sensorId, fromUtcMs, nowUtcMs, maxPoints);
        if (series is null)
        {
            var known = _reader.KnownSensorIds();
            var list = known.Count == 0 ? "(none recorded yet)" : string.Join(", ", known);
            return Task.FromResult(McpToolExecutionResult.Error($"Unknown sensorId '{sensorId}'. Recorded sensors: {list}."));
        }

        var result = new McpQuerySensorHistoryResult
        {
            SensorId = series.SensorId,
            Name = series.Name,
            Unit = series.Unit,
            Tier = TierName(series.Tier),
            Points = series.Points.Select(p => new McpHistoryPoint { T = p.TUtcMs, Value = p.Value }).ToList(),
        };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpQuerySensorHistoryResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }

    internal static string TierName(AiHistoryTier tier) => tier switch
    {
        AiHistoryTier.Raw => "raw",
        AiHistoryTier.OneMinute => "1m",
        _ => "5m",
    };
}
