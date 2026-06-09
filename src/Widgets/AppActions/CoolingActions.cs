using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using static Nexus.Service.Widgets.AppActions.AppActionHelpers;
// Persistence also defines a GraphPoint; the curve model is the Cooling one.
using GraphPoint = Nexus.Service.Models.Cooling.GraphPoint;

namespace Nexus.Service.Widgets.AppActions;

/// <summary>Host actions for cooling: read fan channels + temperature
/// sources, set a manual fan duty, apply a built-in profile. Gated through
/// the manifest's capabilities.dispatch allowlist.</summary>
public static class CoolingActions
{
    public static void RegisterAll(AppActionRegistry registry)
    {
        registry.Register("cooling.state", (services, _, _) =>
        {
            var f = services.GetRequiredService<IFanControlProvider>();
            var dto = new AppCoolingStateDto
            {
                Channels = new List<FanChannel>(f.GetFanChannels()),
                Sources = new List<TemperatureSource>(f.GetTemperatureSources()),
            };
            var json = JsonSerializer.Serialize(dto, AppJsonContext.Default.AppCoolingStateDto);
            using var doc = JsonDocument.Parse(json);
            return Task.FromResult<JsonElement?>(doc.RootElement.Clone());
        });

        registry.Register("cooling.setDuty", (services, args, _) =>
        {
            var id = Str(args, "channelId") ?? Str(args, "id");
            if (string.IsNullOrEmpty(id))
                return Task.FromResult<JsonElement?>(Ack(false, "missing channelId"));
            var v = Num(args, "value");
            if (v is null || v < 0 || v > 100)
                return Task.FromResult<JsonElement?>(Ack(false, "value must be 0..100"));
            var f = services.GetRequiredService<IFanControlProvider>();
            var applied = f.SetFanSpeed(id!, (int)v.Value);
            return Task.FromResult<JsonElement?>(Ack(true, applied: applied.ToString()));
        });

        registry.Register("cooling.applyPreset", (services, args, _) =>
        {
            var name = Str(args, "name") ?? Str(args, "preset");
            if (string.IsNullOrEmpty(name))
                return Task.FromResult<JsonElement?>(Ack(false, "missing name"));
            var f = services.GetRequiredService<IFanControlProvider>();
            var store = services.GetRequiredService<IConfigStore>();
            var applied = FanProfiles.Apply(name!, f, store);
            return Task.FromResult<JsonElement?>(Ack(true, applied: applied));
        });

        // Apply a temperature->duty graph curve to a fan channel. `points` is
        // [{temp,speed}] (the SDK ui-curve maps its x/y to temp/speed). Mirrors
        // POST /cooling/curves/set exactly: Sanitize clamps every speed to
        // [0,100], the curve persists, the active preset is re-derived, and the
        // panel is broadcast. This is the curve-editor counterpart to setDuty.
        registry.Register("cooling.setCurve", (services, args, _) =>
        {
            var channelId = Str(args, "channelId") ?? Str(args, "id");
            if (string.IsNullOrEmpty(channelId))
                return Task.FromResult<JsonElement?>(Ack(false, "missing channelId"));
            var sourceId = Str(args, "sourceId") ?? Str(args, "source");
            if (string.IsNullOrEmpty(sourceId))
                return Task.FromResult<JsonElement?>(Ack(false, "missing sourceId"));
            if (args is null || !args.TryGetValue("points", out var ptsEl) || ptsEl.ValueKind != JsonValueKind.Array)
                return Task.FromResult<JsonElement?>(Ack(false, "missing points array"));

            var points = new List<GraphPoint>();
            foreach (var p in ptsEl.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                if (!p.TryGetProperty("temp", out var t) || t.ValueKind != JsonValueKind.Number) continue;
                if (!p.TryGetProperty("speed", out var s) || s.ValueKind != JsonValueKind.Number) continue;
                points.Add(new GraphPoint { Temp = t.GetDouble(), Speed = s.GetDouble() });
            }
            if (points.Count < 2)
                return Task.FromResult<JsonElement?>(Ack(false, "need at least 2 points"));

            var body = new SetCurvesBody
            {
                Curves =
                {
                    new Curve
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = $"app-{channelId}",
                        Type = "Graph",
                        Input = new CurveInput { Id = sourceId!, Type = "Temperature" },
                        Outputs = { new CurveOutput { Id = channelId!, Type = "Fan" } },
                        Graph = new GraphCurve { Points = points },
                    },
                },
            };

            var c = services.GetRequiredService<ICurveProvider>();
            var f = services.GetRequiredService<IFanControlProvider>();
            var store = services.GetRequiredService<IConfigStore>();
            var hub = services.GetRequiredService<MultiplexHub>();
            c.SetCurves(CoolingSafety.Sanitize(body));
            var derived = FanProfiles.DerivePresetFromCurves(store, f);
            store.Update(s => s.Cooling.ActivePreset = derived);
            PanelTopics.BroadcastCooling(hub);
            return Task.FromResult<JsonElement?>(Ack(true, applied: $"graph -> {channelId} ({points.Count} pts)"));
        });
    }

    public static IReadOnlyList<string> AllActions => new[] { "cooling.state", "cooling.setDuty", "cooling.applyPreset", "cooling.setCurve" };
}
