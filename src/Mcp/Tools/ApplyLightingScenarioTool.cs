using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Write tool: starts a shipped shader effect across every synced lighting device.</summary>
public sealed class ApplyLightingScenarioTool : IMcpTool
{
    // Built once from the real shader registry, not a hand-copied list, so the
    // schema enum and validation never drift from what actually ships.
    private static readonly string SchemaJson =
        "{\"type\":\"object\",\"properties\":{" +
        "\"scenario\":{\"type\":\"string\",\"enum\":[" +
        string.Join(",", ShaderLibrary.AllEffectKeys.Select(k => "\"" + k + "\"")) +
        "]}},\"required\":[\"scenario\"],\"additionalProperties\":false}";

    private readonly ILightingProvider _lighting;
    private readonly MultiplexHub _hub;

    public ApplyLightingScenarioTool(ILightingProvider lighting, MultiplexHub hub)
    {
        _lighting = lighting;
        _hub = hub;
    }

    public string Name => "apply_lighting_scenario";
    public string Title => "Apply Lighting Scenario";

    public string Description =>
        "Starts a shipped lighting animation effect (e.g. rainbow, fire, aurora, plasma) across every " +
        "synced RGB device. Use when the user asks for a lighting mood, theme, or names a specific " +
        "effect. For a single flat color use set_static_color instead.";

    public McpCapability Capability => McpCapability.Lighting;
    public bool ReadOnly => false;
    public string InputSchemaJson => SchemaJson;

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var scenario = McpArgs.StringArg(args, "scenario");
        if (string.IsNullOrEmpty(scenario) || !ShaderLibrary.AllEffectKeys.Contains(scenario))
        {
            var known = string.Join(", ", ShaderLibrary.AllEffectKeys);
            return Task.FromResult(McpToolExecutionResult.Error($"Unknown scenario '{scenario}'. Expected one of: {known}."));
        }

        _lighting.StartAnimate(new AnimateHeadlessStart { Effect = scenario, Speed = 50, Persist = true });
        PanelTopics.BroadcastLighting(_hub);

        var result = new McpApplyLightingScenarioResult { Scenario = scenario };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpApplyLightingScenarioResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
