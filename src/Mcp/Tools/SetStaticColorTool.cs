using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Mcp.Tools;

/// <summary>
/// Write tool: fills every synced device with one flat color by snapping the requested color
/// to the nearest shipped solid-color preset (the "simple*" family the lighting page exposes)
/// and applying that preset's look verbatim - the same effect + slot the UI sets when you pick
/// the preset by hand. Selecting the real preset (rather than overriding the white preset with a
/// computed hue) is what makes the change show in the lighting page and drive devices correctly.
/// </summary>
public sealed class SetStaticColorTool : IMcpTool
{
    // Canonical hue (degrees) of each shipped flat-color preset; a requested
    // color snaps to the nearest by hue. A near-gray color snaps to white.
    private static readonly (string Key, string Name, double Hue)[] Presets =
    {
        ("simplered", "red", 0),
        ("simpleorange", "orange", 30),
        ("simpleyellow", "yellow", 55),
        ("simplegreen", "green", 120),
        ("simplecyan", "cyan", 180),
        ("simpleblue", "blue", 225),
        ("simpleviolet", "violet", 275),
        ("simplepink", "pink", 320),
    };
    private const string WhiteKey = "simplewhite";
    // Below this HSV saturation the hue carries no meaning; treat as white.
    private const double WhiteSaturationCutoff = 0.12;

    private readonly ILightingProvider _lighting;
    private readonly IConfigStore _store;
    private readonly MultiplexHub _hub;

    public SetStaticColorTool(ILightingProvider lighting, IConfigStore store, MultiplexHub hub)
    {
        _lighting = lighting;
        _store = store;
        _hub = hub;
    }

    public string Name => "set_static_color";
    public string Title => "Set Static Color";

    public string Description =>
        "Fills every synced RGB device with one flat color, snapping to the nearest built-in " +
        "solid-color preset (red, orange, yellow, green, cyan, blue, violet, pink, white). Use " +
        "when the user asks for a specific solid color (e.g. 'make it red', '#ff8800'). For a " +
        "named animated look use apply_lighting_scenario instead.";

    public McpCapability Capability => McpCapability.Lighting;
    public bool ReadOnly => false;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"color\":{\"type\":\"string\",\"pattern\":\"^#[0-9a-fA-F]{6}$\",\"description\":\"Hex RGB color, e.g. #ff8800. Snaps to the nearest preset.\"}" +
        "},\"required\":[\"color\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var color = McpArgs.StringArg(args, "color");
        if (!TryParseHexColor(color, out var r, out var g, out var b))
        {
            return Task.FromResult(McpToolExecutionResult.Error(
                "'color' must be a hex RGB string like #ff8800."));
        }

        var (key, name) = NearestPreset(r, g, b);
        _store.Update(s =>
        {
            s.Lighting.StaticColor.R = r;
            s.Lighting.StaticColor.G = g;
            s.Lighting.StaticColor.B = b;
        });

        // Apply the preset's own default look, so the persisted state matches
        // the template (no stray delta) and the lighting page shows the preset
        // exactly as a manual pick would.
        var start = new AnimateHeadlessStart { Effect = key, Persist = true };
        var slot = AnimateTemplateDefaults.Slot(key, 0);
        if (slot is not null)
        {
            start.Speed = slot.Speed;
            start.Intensity = slot.Intensity;
            start.Hue = slot.Hue;
            start.Colorize = slot.Colorize;
            start.Saturation = slot.Saturation;
            start.Contrast = slot.Contrast;
            if (slot.Params is { Count: > 0 })
            {
                foreach (var kv in slot.Params)
                {
                    start.Params.Add(new ShaderParam { Name = kv.Key, Value = kv.Value });
                }
            }
        }
        _lighting.StartAnimate(start);
        PanelTopics.BroadcastLighting(_hub);

        var result = new McpSetStaticColorResult { Color = $"#{r:x2}{g:x2}{b:x2}", Preset = name };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpSetStaticColorResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }

    private static (string Key, string Name) NearestPreset(byte r, byte g, byte b)
    {
        var (hue, sat) = RgbToHueSaturation(r, g, b);
        if (sat < WhiteSaturationCutoff)
        {
            return (WhiteKey, "white");
        }
        var bestKey = WhiteKey;
        var bestName = "white";
        var bestDist = double.MaxValue;
        foreach (var (key, name, presetHue) in Presets)
        {
            // Circular hue distance, 0..180 degrees.
            var d = Math.Abs(((hue - presetHue + 540) % 360) - 180);
            if (d < bestDist)
            {
                bestDist = d;
                bestKey = key;
                bestName = name;
            }
        }
        return (bestKey, bestName);
    }

    private static bool TryParseHexColor(string? input, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (input is null || input.Length != 7 || input[0] != '#')
        {
            return false;
        }
        for (var i = 1; i < 7; i++)
        {
            if (!Uri.IsHexDigit(input[i]))
            {
                return false;
            }
        }
        r = Convert.ToByte(input.Substring(1, 2), 16);
        g = Convert.ToByte(input.Substring(3, 2), 16);
        b = Convert.ToByte(input.Substring(5, 2), 16);
        return true;
    }

    // Standard RGB -> HSV: hue in degrees (0..360), saturation 0..1.
    private static (double Hue, double Saturation) RgbToHueSaturation(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;

        double hue;
        if (delta < 1e-9)
        {
            hue = 0;
        }
        else if (max == rf)
        {
            hue = 60 * (((gf - bf) / delta) % 6);
        }
        else if (max == gf)
        {
            hue = 60 * (((bf - rf) / delta) + 2);
        }
        else
        {
            hue = 60 * (((rf - gf) / delta) + 4);
        }
        if (hue < 0)
        {
            hue += 360;
        }

        var saturation = max <= 1e-9 ? 0 : delta / max;
        return (hue, saturation);
    }
}
