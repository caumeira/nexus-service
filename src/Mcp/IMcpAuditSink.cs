using System;

namespace Nexus.Service.Mcp;

/// <summary>Kind values for <see cref="McpAuditEntry.Kind"/>, matching the
/// event log's type field.</summary>
public static class AuditEntryKinds
{
    public const string AiWrite = "ai_write";
    public const string Lifecycle = "lifecycle";
}

/// <summary>One recorded write-tool call, write-tool consent refusal, or
/// AI Integration lifecycle event (enable/disable, token rotate). ToolName
/// carries the tool name for an ai_write entry or the event name for a
/// lifecycle entry.</summary>
public readonly record struct McpAuditEntry(
    string ToolName,
    string ArgsJson,
    bool Success,
    string? ErrorText,
    DateTimeOffset Timestamp,
    string Kind = AuditEntryKinds.AiWrite);

/// <summary>
/// Records every non-read-only <see cref="IMcpTool"/> call - success, tool-level
/// error, and consent-refused attempts alike - plus lifecycle events recorded
/// directly by AiRoutes. <see cref="Nexus.Service.Mcp.History.AiEventAuditSink"/>
/// is the registered sink (query_events reads the same log);
/// <see cref="LoggingMcpAuditSink"/> is a log-only fallback used where a real
/// event log is not wired up, such as tests.
/// </summary>
public interface IMcpAuditSink
{
    void Record(McpAuditEntry entry);
}

/// <summary>Logs audit entries to the service log only. Used as the audit sink
/// in tests that do not need a real history store.</summary>
public sealed class LoggingMcpAuditSink : IMcpAuditSink
{
    public void Record(McpAuditEntry entry)
    {
        var outcome = entry.Success ? "ok" : "error";
        var error = entry.ErrorText is null ? "" : $" error=\"{entry.ErrorText}\"";
        Console.WriteLine($"[mcp-audit] {entry.Timestamp:O} kind={entry.Kind} tool={entry.ToolName} outcome={outcome}{error} args={entry.ArgsJson}");
    }
}
