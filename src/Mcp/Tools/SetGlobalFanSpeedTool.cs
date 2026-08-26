using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Mcp.Tools;

/// <summary>
/// Write tool: sets the global curve speed multiplier. There is no dedicated
/// REST route for this - the UI never exposes it and always saves it as a
/// no-op 1.0 alongside the full curve list at POST /cooling/curves/set. This
/// tool reuses that same ICurveProvider.SetCurves call, resending the
/// unmodified persisted curves with only the multiplier changed.
/// </summary>
public sealed class SetGlobalFanSpeedTool : IMcpTool
{
    private readonly ICurveProvider _curves;
    private readonly IFanControlProvider _fans;
    private readonly IConfigStore _store;
    private readonly MultiplexHub _hub;
    private readonly FeatureGates _gates;

    public SetGlobalFanSpeedTool(ICurveProvider curves, IFanControlProvider fans, IConfigStore store, MultiplexHub hub, FeatureGates? gates = null)
    {
        _curves = curves;
        _fans = fans;
        _store = store;
        _hub = hub;
        _gates = gates ?? FeatureGates.AllEnabled;
    }

    public string Name => "set_global_fan_speed";
    public string Title => "Set Global Fan Speed";

    public string Description =>
        "Scales every curve-driven fan's speed by a global percentage (100 = normal curve speed, " +
        "50 = half speed, 0 = curve-driven fans stopped). Does not affect fans the user has set " +
        "manually. Use when the user asks to make curve-driven cooling faster or slower overall " +
        "without changing the curve shapes themselves.";

    public McpCapability Capability => McpCapability.Cooling;
    public bool ReadOnly => false;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"percent\":{\"type\":\"number\",\"minimum\":0,\"maximum\":100,\"description\":" +
        "\"Global fan curve speed multiplier as a percentage, 0-100.\"}" +
        "},\"required\":[\"percent\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        if (!_gates.Cooling)
        {
            return Task.FromResult(McpToolExecutionResult.Error("Cooling is disabled in Settings."));
        }
        var percent = McpArgs.NumberArg(args, "percent");
        if (percent is null || !double.IsFinite(percent.Value) || percent.Value < 0 || percent.Value > 100)
        {
            return Task.FromResult(McpToolExecutionResult.Error(
                "'percent' is required and must be a number between 0 and 100."));
        }

        var cooling = _store.Load().Cooling;
        var body = new SetCurvesBody
        {
            GlobalSpeedModifier = percent.Value / 100.0,
            Curves = cooling.Curves.Select(McpCurveMapper.ToWireCurve).ToList(),
        };
        _curves.SetCurves(CoolingSafety.Sanitize(body));
        var derived = FanProfiles.DerivePresetFromCurves(_store, _fans);
        _store.Update(s => s.Cooling.ActivePreset = derived);
        PanelTopics.BroadcastCooling(_hub);

        var result = new McpSetGlobalFanSpeedResult { Percent = (int)System.Math.Round(percent.Value) };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpSetGlobalFanSpeedResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
