using System.Collections.Generic;

namespace Nexus.Service.Mcp.History;

/// <summary>One row recorded into the event log: an audited MCP write call or a lifecycle event.</summary>
public readonly record struct AiHistoryEventRow(
    long TsUtcMs, string Kind, string Name, string ArgsJson, bool Success, string? ErrorText);

public sealed record AiHistoryEventQueryResult(IReadOnlyList<AiHistoryEventRow> Events, bool Truncated);

/// <summary>
/// Append-only audit trail for every MCP write-tool call and AI Integration
/// lifecycle event (enable/disable, token rotate) - query_events' backing
/// store. The implementation is
/// <see cref="Nexus.Service.Mcp.History.Binary.BinaryAiEventLog"/>; when it
/// can't be opened, DI falls back to <see cref="UnavailableAiEventLog"/> so
/// the rest of the service keeps running and query_events reports
/// unavailability instead of throwing. Sensor telemetry no longer goes
/// through this log or any AI-specific store - query_sensor_history and
/// get_history_summary read the always-on monitoring store directly via
/// <see cref="MonitoringSensorHistoryReader"/>.
/// </summary>
public interface IAiEventLog
{
    /// <summary>False only for the unavailable fallback. Every write and query
    /// method on that fallback is a safe no-op / empty result.</summary>
    bool IsAvailable { get; }

    void RecordEvent(AiHistoryEventRow row);

    /// <summary>Newest first, capped at limit + 1 internally to compute Truncated.</summary>
    AiHistoryEventQueryResult QueryEvents(long fromUtcMs, string? type, int limit);
}
