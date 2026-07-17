using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using GraphPoint = Nexus.Service.Models.Cooling.GraphPoint;

namespace Nexus.Service.Mcp.Tools;

/// <summary>
/// Write tool: creates or updates a temperature/speed fan curve and attaches
/// it to the given fan channels, through the same ICurveProvider.SetCurves
/// full-replace call POST /cooling/curves/set uses. SetCurves clears and
/// re-adds the whole curve list every call, so the one-curve-per-output
/// invariant is enforced here the same way the dashboard's curve editor does
/// it client-side: strip the target outputs from every other curve before
/// resending the full set.
/// </summary>
public sealed class SetFanCurveTool : IMcpTool
{
    private const double MinTempC = 0;
    private const double MaxTempC = 150;
    private const string DefaultCurveName = "AI Fan Curve";

    // Culture-invariant so a comma-decimal OS culture never emits a
    // comma into schema JSON written raw via WriteRawValue.
    private static readonly string MinTempCJson = MinTempC.ToString(CultureInfo.InvariantCulture);
    private static readonly string MaxTempCJson = MaxTempC.ToString(CultureInfo.InvariantCulture);

    private readonly ICurveProvider _curves;
    private readonly IFanControlProvider _fans;
    private readonly IConfigStore _store;
    private readonly MultiplexHub _hub;

    public SetFanCurveTool(ICurveProvider curves, IFanControlProvider fans, IConfigStore store, MultiplexHub hub)
    {
        _curves = curves;
        _fans = fans;
        _store = store;
        _hub = hub;
    }

    public string Name => "set_fan_curve";
    public string Title => "Set Fan Curve";

    public string Description =>
        "Creates or updates a temperature-to-speed fan curve and attaches it to the given fan " +
        "channels, detaching them from whatever curve or preset previously drove them. Use when the " +
        "user describes a custom cooling behavior (e.g. 'keep the GPU fans quiet under 60C then ramp " +
        "up'). Call get_cooling_state first to find valid fan channel ids. Passing 'name' matching an " +
        "existing curve updates it in place instead of creating a duplicate.";

    public McpCapability Capability => McpCapability.Cooling;
    public bool ReadOnly => false;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"points\":{\"type\":\"array\",\"minItems\":1,\"items\":{\"type\":\"object\"," +
        "\"properties\":{\"temp\":{\"type\":\"number\",\"minimum\":" + MinTempCJson + ",\"maximum\":" + MaxTempCJson + "}," +
        "\"speed\":{\"type\":\"number\",\"minimum\":0,\"maximum\":100}}," +
        "\"required\":[\"temp\",\"speed\"]},\"description\":\"Temperature (Celsius) to fan speed (percent) points, at least one.\"}," +
        "\"outputs\":{\"type\":\"array\",\"minItems\":1,\"items\":{\"type\":\"string\"},\"description\":\"Fan channel ids from get_cooling_state.\"}," +
        "\"input\":{\"type\":\"string\",\"enum\":[\"cpu\",\"gpu\"],\"description\":\"Temperature source category driving the curve. Defaults to cpu.\"}," +
        "\"name\":{\"type\":\"string\",\"description\":\"Curve label. Reuses an existing curve with this name instead of creating a new one.\"}" +
        "},\"required\":[\"points\",\"outputs\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var pointsResult = ParsePoints(args);
        if (pointsResult.Error is not null)
        {
            return Task.FromResult(McpToolExecutionResult.Error(pointsResult.Error));
        }

        var outputsResult = ParseOutputs(args, _fans);
        if (outputsResult.Error is not null)
        {
            return Task.FromResult(McpToolExecutionResult.Error(outputsResult.Error));
        }

        var inputArg = McpArgs.StringArg(args, "input");
        if (inputArg is not null && inputArg != "cpu" && inputArg != "gpu")
        {
            return Task.FromResult(McpToolExecutionResult.Error("'input' must be 'cpu' or 'gpu'."));
        }
        var (inputSource, inputError) = ResolveInput(inputArg ?? "cpu", _fans.GetTemperatureSources());
        if (inputError is not null)
        {
            return Task.FromResult(McpToolExecutionResult.Error(inputError));
        }

        var name = McpArgs.StringArg(args, "name");
        var targetName = string.IsNullOrWhiteSpace(name) ? DefaultCurveName : name.Trim();

        var cooling = _store.Load().Cooling;
        var wireCurves = cooling.Curves.Select(McpCurveMapper.ToWireCurve).ToList();
        var outputIds = outputsResult.OutputIds!;
        foreach (var c in wireCurves)
        {
            c.Outputs.RemoveAll(o => outputIds.Contains(o.Id));
        }

        var target = wireCurves.FirstOrDefault(c =>
            c.Preset is null && string.Equals(c.Name, targetName, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            target = new Curve { Id = "curve-" + Guid.NewGuid().ToString("N")[..8] };
            wireCurves.Add(target);
        }
        target.Name = targetName;
        target.Type = "Graph";
        target.Input = new CurveInput { Id = inputSource!.Id, Type = "Temperature", Device = inputSource.Category };
        target.Outputs = outputIds.Select(id => new CurveOutput { Id = id, Type = "Fan" }).ToList();
        target.Graph = new GraphCurve { Points = pointsResult.Points! };
        target.Preset = null;

        var body = new SetCurvesBody { GlobalSpeedModifier = cooling.GlobalSpeedModifier, Curves = wireCurves };
        _curves.SetCurves(CoolingSafety.Sanitize(body));
        var derived = FanProfiles.DerivePresetFromCurves(_store, _fans);
        _store.Update(s => s.Cooling.ActivePreset = derived);
        PanelTopics.BroadcastCooling(_hub);

        var result = new McpSetFanCurveResult { CurveId = target.Id, Name = target.Name, Outputs = outputIds };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpSetFanCurveResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }

    private static (List<GraphPoint>? Points, string? Error) ParsePoints(JsonElement? args)
    {
        if (!McpArgs.TryGetArray(args, "points", out var pointsEl) || pointsEl.GetArrayLength() == 0)
        {
            return (null, "'points' is required: a non-empty array of {temp, speed} objects.");
        }

        var points = new List<GraphPoint>();
        foreach (var p in pointsEl.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object
                || !p.TryGetProperty("temp", out var tEl) || tEl.ValueKind != JsonValueKind.Number
                || !p.TryGetProperty("speed", out var sEl) || sEl.ValueKind != JsonValueKind.Number)
            {
                return (null, "Each point requires numeric 'temp' and 'speed'.");
            }

            var temp = tEl.GetDouble();
            var speed = sEl.GetDouble();
            if (!double.IsFinite(temp) || temp < MinTempC || temp > MaxTempC)
            {
                return (null, $"Point temp {temp} is out of range ({MinTempC}-{MaxTempC}).");
            }
            if (!double.IsFinite(speed) || speed < 0 || speed > 100)
            {
                return (null, $"Point speed {speed} is out of range (0-100).");
            }
            points.Add(new GraphPoint { Temp = temp, Speed = speed });
        }
        return (points, null);
    }

    private static (List<string>? OutputIds, string? Error) ParseOutputs(JsonElement? args, IFanControlProvider fans)
    {
        if (!McpArgs.TryGetArray(args, "outputs", out var outputsEl) || outputsEl.GetArrayLength() == 0)
        {
            return (null, "'outputs' is required: a non-empty array of fan channel ids.");
        }

        var outputIds = new List<string>();
        foreach (var o in outputsEl.EnumerateArray())
        {
            if (o.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(o.GetString()))
            {
                return (null, "'outputs' entries must be non-empty strings.");
            }
            outputIds.Add(o.GetString()!);
        }

        var validIds = fans.GetFanChannels().Select(c => c.Id).ToList();
        var validSet = new HashSet<string>(validIds, StringComparer.Ordinal);
        var unknown = outputIds.Where(id => !validSet.Contains(id)).ToList();
        if (unknown.Count > 0)
        {
            var known = validIds.Count == 0 ? "(none detected)" : string.Join(", ", validIds);
            return (null, $"Unknown fan channel id(s): {string.Join(", ", unknown)}. Known channel ids: {known}.");
        }
        return (outputIds, null);
    }

    private static (TemperatureSource? Source, string? Error) ResolveInput(
        string category, IReadOnlyList<TemperatureSource> sources)
    {
        var wantCategory = category == "gpu" ? "GPU" : "CPU";
        var preferred = sources.FirstOrDefault(t =>
            t.Category == wantCategory && t.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
            ?? sources.FirstOrDefault(t => t.Category == wantCategory);
        return preferred is null
            ? (null, $"No {wantCategory} temperature source is available on this machine.")
            : (preferred, null);
    }
}
