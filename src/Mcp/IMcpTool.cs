using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Mcp;

/// <summary>
/// One MCP tool. <see cref="McpToolRegistry"/> discovers every implementation
/// registered in DI (multi-registered IMcpTool), serves them from tools/list,
/// and dispatches tools/call by <see cref="Name"/>.
/// </summary>
public interface IMcpTool
{
    /// <summary>Wire name, e.g. "get_system_overview". Stable - an MCP client may pin it.</summary>
    string Name { get; }

    /// <summary>Short human-readable title for client UIs.</summary>
    string Title { get; }

    /// <summary>Explains what the tool returns AND when to call it, so a model
    /// can choose between tools without trial and error.</summary>
    string Description { get; }

    /// <summary>Consent domain this tool belongs to; gates every call live against
    /// the matching AiIntegrationSettings.Allow* flag.</summary>
    McpCapability Capability { get; }

    /// <summary>True for a tool that only reads state. Drives the wire
    /// annotations (readOnlyHint / destructiveHint) and whether the call is audited.</summary>
    bool ReadOnly { get; }

    /// <summary>Raw JSON Schema (object type) describing the tool's arguments.
    /// Written verbatim onto the wire - must be valid JSON on its own.</summary>
    string InputSchemaJson { get; }

    /// <summary>
    /// Runs the tool. <paramref name="args"/> is the parsed "arguments" object
    /// from tools/call, or null when the call omitted it. Input-validation
    /// failures (bad enum value, unknown id) are reported via
    /// <see cref="McpToolExecutionResult.Error"/>, not by throwing - a thrown
    /// exception surfaces as an internal error to the caller instead of an
    /// actionable tool result.
    /// </summary>
    Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct);
}

/// <summary>Result of running an <see cref="IMcpTool"/>: the text that becomes
/// the tool result's content[0].text, and whether it represents a tool-level error.</summary>
public readonly record struct McpToolExecutionResult(string Text, bool IsError)
{
    public static McpToolExecutionResult Ok(string text) => new(text, false);
    public static McpToolExecutionResult Error(string text) => new(text, true);
}
