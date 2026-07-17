using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Persistence;

namespace Nexus.Service.Mcp;

/// <summary>
/// Owns the set of <see cref="IMcpTool"/> implementations registered in DI and
/// dispatches tools/call: live consent check against the current
/// AiIntegrationSettings, execute, then audit non-read-only calls (including
/// consent refusals).
/// </summary>
public sealed class McpToolRegistry
{
    private readonly Dictionary<string, IMcpTool> _byName;
    private readonly IConfigStore _store;
    private readonly IMcpAuditSink _audit;

    public McpToolRegistry(IEnumerable<IMcpTool> tools, IConfigStore store, IMcpAuditSink audit)
    {
        _store = store;
        _audit = audit;
        _byName = new Dictionary<string, IMcpTool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            _byName[tool.Name] = tool;
        }
    }

    public IReadOnlyCollection<IMcpTool> Tools => _byName.Values;

    public bool TryGetTool(string name, out IMcpTool tool) => _byName.TryGetValue(name, out tool!);

    public async Task<McpToolExecutionResult> CallAsync(IMcpTool tool, JsonElement? args, CancellationToken ct)
    {
        var settings = _store.Load().AiIntegration;
        if (!tool.Capability.IsAllowed(settings))
        {
            var message = $"{tool.Capability.ToggleLabel()} is turned off in AI Integration settings. " +
                           $"Enable it to use the {tool.Name} tool.";
            if (!tool.ReadOnly)
            {
                _audit.Record(new McpAuditEntry(tool.Name, ArgsToJson(args), false, message, DateTimeOffset.UtcNow));
            }
            return McpToolExecutionResult.Error(message);
        }

        McpToolExecutionResult result;
        try
        {
            result = await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!tool.ReadOnly)
            {
                _audit.Record(new McpAuditEntry(tool.Name, ArgsToJson(args), false, ex.Message, DateTimeOffset.UtcNow));
            }
            throw new McpToolExecutionException(tool.Name, ex);
        }

        if (!tool.ReadOnly)
        {
            _audit.Record(new McpAuditEntry(
                tool.Name, ArgsToJson(args), !result.IsError, result.IsError ? result.Text : null, DateTimeOffset.UtcNow));
        }
        return result;
    }

    private static string ArgsToJson(JsonElement? args) => args is { } a ? a.GetRawText() : "{}";
}

/// <summary>
/// Thrown by <see cref="McpToolRegistry.CallAsync"/> when a tool throws instead
/// of returning <see cref="McpToolExecutionResult.Error"/>. Message carries only
/// the tool name plus the inner exception's type and message (no stack trace),
/// since McpServerHost writes it straight onto the wire as a JSON-RPC error.
/// </summary>
internal sealed class McpToolExecutionException : Exception
{
    public McpToolExecutionException(string toolName, Exception inner)
        : base($"Tool '{toolName}' failed: {inner.GetType().Name}: {inner.Message}", inner)
    {
    }
}
