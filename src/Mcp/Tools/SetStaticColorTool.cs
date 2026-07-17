using System;
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
/// Write tool: fills every synced device with one flat color. No route or engine path sets
/// Lighting.StaticColor directly - the "simple*" shaders are the only mechanism that render
/// an exact solid color (simple.frag: one HSV fill driven entirely by the Hue/Saturation
/// StartAnimate params), so this converts hex to hue/saturation and drives that shader. The
/// shader pins HSV value at 1.0, so a dark target color desaturates toward white instead of
/// dimming; pair with set_brightness for overall dimming.
/// </summary>
public sealed class SetStaticColorTool : IMcpTool
{
    private const string NeutralEffectKey = "simplewhite";

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
        "Fills every synced RGB device with one flat color. Use when the user asks for a specific " +
        "solid color (e.g. 'make it red', '#ff8800'). For a named animated look use " +
        "apply_lighting_scenario instead.";

    public McpCapability Capability => McpCapability.Lighting;
    public bool ReadOnly => false;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"color\":{\"type\":\"string\",\"pattern\":\"^#[0-9a-fA-F]{6}$\",\"description\":\"Hex RGB color, e.g. #ff8800.\"}" +
        "},\"required\":[\"color\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var color = McpArgs.StringArg(args, "color");
        if (!TryParseHexColor(color, out var r, out var g, out var b))
        {
            return Task.FromResult(McpToolExecutionResult.Error(
                "'color' must be a hex RGB string like #ff8800."));
        }

        var (hue, saturation) = RgbToHueSaturation(r, g, b);
        _store.Update(s =>
        {
            s.Lighting.StaticColor.R = r;
            s.Lighting.StaticColor.G = g;
            s.Lighting.StaticColor.B = b;
        });
        _lighting.StartAnimate(new AnimateHeadlessStart
        {
            Effect = NeutralEffectKey,
            Hue = hue,
            Saturation = saturation,
            Persist = true,
        });
        PanelTopics.BroadcastLighting(_hub);

        var result = new McpSetStaticColorResult { Color = $"#{r:x2}{g:x2}{b:x2}" };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpSetStaticColorResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
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

    // Standard RGB -> HSV, hue normalized 0..1 to match u_hue's wrap convention
    // in _prelude.frag's hsv2rgb.
    private static (float Hue, float Saturation) RgbToHueSaturation(byte r, byte g, byte b)
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
            hue = (((gf - bf) / delta) % 6) / 6.0;
        }
        else if (max == gf)
        {
            hue = (((bf - rf) / delta) + 2) / 6.0;
        }
        else
        {
            hue = (((rf - gf) / delta) + 4) / 6.0;
        }
        if (hue < 0)
        {
            hue += 1.0;
        }

        var saturation = max <= 1e-9 ? 0 : delta / max;
        return ((float)hue, (float)saturation);
    }
}
