using System;

namespace Nexus.Service.Mcp.History;

/// <summary>
/// Fallback used when the event log file can't be opened. Every write is a
/// no-op and every query returns empty; IsAvailable is false so query_events
/// and AiEventAuditSink can report unavailability instead of silently
/// returning empty data that reads as "no events recorded yet".
/// </summary>
public sealed class UnavailableAiEventLog : IAiEventLog
{
    public bool IsAvailable => false;

    public void RecordEvent(AiHistoryEventRow row) { }

    public AiHistoryEventQueryResult QueryEvents(long fromUtcMs, string? type, int limit) =>
        new(Array.Empty<AiHistoryEventRow>(), false);
}
