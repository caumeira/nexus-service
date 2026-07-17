using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Persistence;

namespace Nexus.Service.Mcp.Assistant;

/// <summary>
/// The agentic query loop: builds MCP tool definitions from the live
/// <see cref="McpToolRegistry"/>, drives Ollama's /api/chat tool-calling
/// protocol, and runs every requested tool call THROUGH the registry so
/// consent + audit apply exactly as they do for the MCP listener. Bounded by
/// a hard round cap and a per-query timeout so a looping model can't hang a
/// request forever.
/// </summary>
public sealed class LocalAssistant
{
    public const int MaxRounds = 6;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMinutes(3);

    private readonly McpToolRegistry _registry;
    private readonly IConfigStore _store;
    private readonly OllamaRuntimeManager _runtime;

    public LocalAssistant(McpToolRegistry registry, IConfigStore store, OllamaRuntimeManager runtime)
    {
        _registry = registry;
        _store = store;
        _runtime = runtime;
    }

    public async Task<AssistantQueryOutcome> RunAsync(string prompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return AssistantQueryOutcome.Refused("A prompt is required.");
        }

        var settings = _store.Load().AiIntegration;
        if (!settings.Enabled)
        {
            return AssistantQueryOutcome.Refused("AI Integration is turned off. Enable it in settings to use the assistant.");
        }

        var model = settings.AssistantActiveModel;
        if (string.IsNullOrEmpty(model))
        {
            return AssistantQueryOutcome.Refused("No assistant model is selected.");
        }

        var client = _runtime.Client;
        if (_runtime.State != AssistantRuntimeState.Running || client is null)
        {
            return AssistantQueryOutcome.Refused("The local assistant runtime is not running.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(QueryTimeout);

        var tools = BuildToolDefs();
        var messages = new List<OllamaChatMessage> { new() { Role = "user", Content = prompt } };
        var toolsRun = new List<AssistantToolRunDto>();

        try
        {
            for (var round = 0; round < MaxRounds; round++)
            {
                var response = await client.ChatAsync(model, messages, tools, cts.Token).ConfigureAwait(false);
                var msg = response.Message;

                if (msg.ToolCalls is { Count: > 0 } calls)
                {
                    messages.Add(msg);
                    foreach (var call in calls)
                    {
                        var (text, ok) = await RunToolCallAsync(call, cts.Token).ConfigureAwait(false);
                        toolsRun.Add(new AssistantToolRunDto
                        {
                            Name = call.Function.Name,
                            Args = call.Function.Arguments?.GetRawText() ?? "{}",
                            Ok = ok,
                        });
                        messages.Add(new OllamaChatMessage { Role = "tool", ToolName = call.Function.Name, Content = text });
                    }
                    continue;
                }

                return AssistantQueryOutcome.Success(new AssistantQueryResponse
                {
                    Answer = AssistantReasoningStripper.Strip(msg.Content ?? ""),
                    ToolsRun = toolsRun,
                });
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return AssistantQueryOutcome.Refused("The assistant timed out.");
        }
        catch (Exception ex)
        {
            return AssistantQueryOutcome.Refused($"The assistant request failed: {ex.Message}");
        }

        return AssistantQueryOutcome.Success(new AssistantQueryResponse
        {
            Answer = $"Reached the {MaxRounds}-round tool-call limit without a final answer. Try a narrower question.",
            ToolsRun = toolsRun,
        });
    }

    private async Task<(string Text, bool Ok)> RunToolCallAsync(OllamaToolCall call, CancellationToken ct)
    {
        var name = call.Function.Name;
        if (!_registry.TryGetTool(name, out var tool))
        {
            return ($"Unknown tool '{name}'.", false);
        }
        try
        {
            var result = await _registry.CallAsync(tool, call.Function.Arguments, ct).ConfigureAwait(false);
            return (result.Text, !result.IsError);
        }
        catch (McpToolExecutionException ex)
        {
            return (ex.Message, false);
        }
    }

    private List<OllamaToolDef> BuildToolDefs()
    {
        var defs = new List<OllamaToolDef>();
        foreach (var tool in _registry.Tools)
        {
            using var doc = JsonDocument.Parse(tool.InputSchemaJson);
            defs.Add(new OllamaToolDef
            {
                Type = "function",
                Function = new OllamaFunctionDef
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    Parameters = doc.RootElement.Clone(),
                },
            });
        }
        return defs;
    }
}

/// <summary>Result of one <see cref="LocalAssistant.RunAsync"/> call: either a
/// completed answer or a structured refusal (master toggle off, no model ready,
/// empty prompt, timeout, or an underlying chat failure).</summary>
public readonly struct AssistantQueryOutcome
{
    public bool IsRefused { get; init; }
    public string? RefusalReason { get; init; }
    public AssistantQueryResponse? Response { get; init; }

    public static AssistantQueryOutcome Success(AssistantQueryResponse response) => new() { IsRefused = false, Response = response };
    public static AssistantQueryOutcome Refused(string reason) => new() { IsRefused = true, RefusalReason = reason };
}

/// <summary>Strips Qwen3.5's <c>&lt;think&gt;...&lt;/think&gt;</c> reasoning block from a
/// final answer before it reaches the user. Also strips a trailing
/// unterminated <c>&lt;think&gt;</c> with no closing tag, which a
/// timeout-cut response leaves behind.</summary>
internal static partial class AssistantReasoningStripper
{
    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex(@"<think>.*$", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex UnterminatedThinkBlock();

    public static string Strip(string content)
    {
        var stripped = ThinkBlock().Replace(content, "");
        stripped = UnterminatedThinkBlock().Replace(stripped, "");
        return stripped.Trim();
    }
}
