using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Write tool: the same stop ILightingProvider.StopAll POST /lighting/stop calls.</summary>
public sealed class StopLightingTool : IMcpTool
{
    private readonly ILightingProvider _lighting;
    private readonly MultiplexHub _hub;

    public StopLightingTool(ILightingProvider lighting, MultiplexHub hub)
    {
        _lighting = lighting;
        _hub = hub;
    }

    public string Name => "stop_lighting";
    public string Title => "Stop Lighting";

    public string Description =>
        "Stops every running lighting effect and turns the LEDs off. Use when the user asks to turn " +
        "off, stop, or disable the lights or RGB.";

    public McpCapability Capability => McpCapability.Lighting;
    public bool ReadOnly => false;
    public string InputSchemaJson => "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        _lighting.StopAll();
        PanelTopics.BroadcastLighting(_hub);

        var result = new McpStopLightingResult { Stopped = true };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpStopLightingResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
