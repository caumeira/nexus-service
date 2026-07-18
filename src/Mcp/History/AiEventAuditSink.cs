using System;

namespace Nexus.Service.Mcp.History;

/// <summary>
/// Persists every audited MCP event (write-tool calls and lifecycle events)
/// into IAiEventLog, replacing the phase-1 LoggingMcpAuditSink in DI. Still
/// logs to the service log so a live tail shows AI activity without querying
/// the event log.
/// </summary>
public sealed class AiEventAuditSink : IMcpAuditSink
{
    private readonly IAiEventLog _events;

    public AiEventAuditSink(IAiEventLog events) => _events = events;

    public void Record(McpAuditEntry entry)
    {
        // Auditing is best-effort: a runtime store failure (disk full, log
        // corruption past open) must not turn tool calls or /ai/config into
        // 500s. The service-log line below still lands, so a dropped row is
        // loud in a live tail.
        try
        {
            _events.RecordEvent(new AiHistoryEventRow(
                entry.Timestamp.ToUnixTimeMilliseconds(), entry.Kind, entry.ToolName, entry.ArgsJson, entry.Success, entry.ErrorText));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[mcp-audit] ERR failed to persist audit event: {ex.Message}");
        }

        var outcome = entry.Success ? "ok" : "error";
        var error = entry.ErrorText is null ? "" : $" error=\"{entry.ErrorText}\"";
        Console.WriteLine($"[mcp-audit] {entry.Timestamp:O} kind={entry.Kind} name={entry.ToolName} outcome={outcome}{error} args={entry.ArgsJson}");
    }
}
