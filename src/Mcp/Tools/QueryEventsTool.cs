using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Mcp.History;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only history tool: the audit event log - AI write-tool calls and
/// lifecycle events (enable/disable, token rotate) - newest first.</summary>
public sealed class QueryEventsTool : IMcpTool
{
    internal const int DefaultLimit = 50;
    internal const int MaxAllowedLimit = 500;
    private const int MaxAllowedMinutes = AiHistoryRetention.FiveMinuteTierRetentionMinutes;

    private static readonly string[] ValidTypes = { AuditEntryKinds.AiWrite, AuditEntryKinds.Lifecycle };

    private readonly IAiHistoryStore _history;

    public QueryEventsTool(IAiHistoryStore history) => _history = history;

    public string Name => "query_events";
    public string Title => "Audit Events";

    public string Description =>
        "Returns the AI Integration audit log - every write tool call (apply_cooling_preset, " +
        "set_fan_curve, apply_profile, etc.) and lifecycle event (AI Integration enabled/disabled, " +
        "token rotated) - newest first, over a window in minutes. Use to answer what changes have " +
        "already been made and whether a change succeeded.";

    public McpCapability Capability => McpCapability.History;
    public bool ReadOnly => true;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"minutes\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"How far back to look, in minutes.\"}," +
        "\"type\":{\"type\":\"string\",\"enum\":[\"ai_write\",\"lifecycle\"],\"description\":\"Filter to only this event type.\"}," +
        "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"Maximum events to return, newest first.\"}" +
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

        var type = McpArgs.StringArg(args, "type");
        if (type is not null && !ValidTypes.Contains(type, StringComparer.Ordinal))
        {
            return Task.FromResult(McpToolExecutionResult.Error($"'type' must be one of: {string.Join(", ", ValidTypes)}."));
        }

        var limit = McpArgs.IntArg(args, "limit") is { } requested && requested > 0
            ? Math.Min(requested, MaxAllowedLimit)
            : DefaultLimit;

        var nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fromUtcMs = nowUtcMs - minutes * 60_000L;

        var queryResult = _history.QueryEvents(fromUtcMs, type, limit);
        var result = new McpQueryEventsResult
        {
            Truncated = queryResult.Truncated,
            Events = queryResult.Events.Select(e => new McpHistoryEvent
            {
                TUtc = e.TsUtcMs,
                Type = e.Kind,
                Name = e.Name,
                ArgsJson = e.ArgsJson,
                Success = e.Success,
                ErrorText = e.ErrorText,
            }).ToList(),
        };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpQueryEventsResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
