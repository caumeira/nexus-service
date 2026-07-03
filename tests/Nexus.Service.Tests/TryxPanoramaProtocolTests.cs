using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxPanoramaProtocolTests
{
    // Extracts the JSON body from a framed STATE-all or POST-config packet.
    private static string ExtractJson(byte[] frame)
    {
        // Destuff: 0x5B 0x01 -> 0x5A, 0x5B 0x02 -> 0x5B (skip outer 0x5A markers)
        var inner = new List<byte>();
        for (var i = 1; i < frame.Length - 1; i++)
        {
            if (frame[i] == 0x5B && i + 1 < frame.Length - 1)
            {
                inner.Add(frame[i + 1] == 0x01 ? (byte)0x5A : (byte)0x5B);
                i++;
            }
            else
            {
                inner.Add(frame[i]);
            }
        }
        // Skip LEN_HI LEN_LO and trailing CRC; body = inner[2..^1].
        var bodyBytes = inner.Skip(2).Take(inner.Count - 3).ToArray();
        var body = Encoding.UTF8.GetString(bodyBytes);
        // Body is HTTP-like headers + blank line + JSON.
        var jsonStart = body.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return jsonStart >= 0 ? body.Substring(jsonStart + 4) : body;
    }

    [Fact]
    public void BuildStateAll_no_arg_produces_valid_json_with_required_fields()
    {
        var frame = TryxPanoramaProtocol.BuildStateAll();
        var json = ExtractJson(frame);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Core objects present
        Assert.True(root.TryGetProperty("cpu", out var cpu));
        Assert.True(root.TryGetProperty("gpu", out _));
        Assert.True(root.TryGetProperty("memory", out _));
        Assert.True(root.TryGetProperty("motherboard", out var mobo));

        // New fields present with zero values in the zero-fill path
        Assert.True(cpu.TryGetProperty("usage", out var usage));
        Assert.Equal(0, usage.GetInt32());
        Assert.True(mobo.TryGetProperty("pchTemperature", out var pch));
        Assert.Equal(0, pch.GetInt32());
        Assert.True(root.TryGetProperty("timestamp", out var ts));
        Assert.Equal(0, ts.GetInt64());
    }

    [Fact]
    public void BuildStateAll_with_sensor_json_uses_provided_json()
    {
        var sensorJson = "{\"cpu\":{\"load\":42,\"usage\":42,\"temperature\":75,\"speedAverage\":3600,\"power\":65,\"voltage\":1}," +
                         "\"gpu\":{\"load\":80,\"temperature\":68,\"fan\":0,\"speed\":1800,\"power\":200,\"voltage\":1}," +
                         "\"memory\":{\"total\":32,\"used\":16,\"load\":50,\"temperature\":0,\"speed\":0}," +
                         "\"disk\":{\"total\":0,\"used\":0,\"load\":0,\"activity\":0,\"temperature\":0,\"readSpeed\":0,\"writeSpeed\":0}," +
                         "\"fans\":[{\"onBoard\":true,\"type\":\"Fan\",\"name\":\"Fan CPU\",\"value\":0}]," +
                         "\"motherboard\":{\"temperature\":35,\"pchTemperature\":40}," +
                         "\"network\":{\"upload\":0,\"download\":0}," +
                         "\"timestamp\":1234567890000}";

        var frame = TryxPanoramaProtocol.BuildStateAll(sensorJson);
        var json = ExtractJson(frame);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(42, root.GetProperty("cpu").GetProperty("load").GetInt32());
        Assert.Equal(42, root.GetProperty("cpu").GetProperty("usage").GetInt32());
        Assert.Equal(75, root.GetProperty("cpu").GetProperty("temperature").GetInt32());
        Assert.Equal(1234567890000L, root.GetProperty("timestamp").GetInt64());
        Assert.Equal(40, root.GetProperty("motherboard").GetProperty("pchTemperature").GetInt32());
    }

    [Fact]
    public void BuildConfigPreset_with_default_overlay_emits_correct_settings_and_sysinfoDisplay()
    {
        var overlay = new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { Label = "CPU Temperature" }],
            Color = "#ffffff",
            Align = "Center",
            Filter = null,
            Opacity = 100,
        };

        var frame = TryxPanoramaProtocol.BuildConfigPreset(80, "Pre-set 1: Cooling delivery", overlay);
        var json = ExtractJson(frame);
        var doc = JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("waterBlockScreen").GetProperty("id");

        var settings = id.GetProperty("settings");
        Assert.Equal("#ffffff", settings.GetProperty("color").GetString());
        Assert.Equal("Center", settings.GetProperty("align").GetString());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("filter").GetProperty("value").ValueKind);
        Assert.Equal(100, settings.GetProperty("filter").GetProperty("opacity").GetInt32());

        var display = id.GetProperty("sysinfoDisplay");
        Assert.Equal(JsonValueKind.Array, display.ValueKind);
        Assert.Equal(1, display.GetArrayLength());
        Assert.Equal("CPU Temperature", display[0].GetString());
    }

    [Fact]
    public void BuildConfigCustom_with_empty_overlay_emits_empty_sysinfoDisplay()
    {
        var overlay = new TryxOverlayConfig { Items = [], Color = "#000000", Align = "Left" };
        var frame = TryxPanoramaProtocol.BuildConfigCustom(100, "clip.mp4", overlay);
        var json = ExtractJson(frame);
        var doc = JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("waterBlockScreen").GetProperty("id");

        Assert.Equal("Customization", id.GetProperty("id").GetString());
        var display = id.GetProperty("sysinfoDisplay");
        Assert.Equal(0, display.GetArrayLength());
    }

    [Fact]
    public void BuildConfigFanFixed_threads_overlay_to_id_object()
    {
        var overlay = new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { Label = "GPU Temperature" }, new TryxOverlaySensorItem { Label = "CPU Load" }],
            Color = "#ff0000",
            Align = "Right",
        };
        var frame = TryxPanoramaProtocol.BuildConfigFanFixed(50, "myfile.mp4", isCustom: true, overlay, fixedPercent: 60);
        var json = ExtractJson(frame);
        var doc = JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("waterBlockScreen").GetProperty("id");

        Assert.Equal("#ff0000", id.GetProperty("settings").GetProperty("color").GetString());
        Assert.Equal(2, id.GetProperty("sysinfoDisplay").GetArrayLength());
        Assert.Equal("GPU Temperature", id.GetProperty("sysinfoDisplay")[0].GetString());
    }

    [Fact]
    public void BuildSysinfoDisplay_survives_strings_needing_json_escaping()
    {
        var overlay = new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { Label = "CPU \"Package\"" }, new TryxOverlaySensorItem { Label = "Temp\\C" }],
            Color = "#000000",
            Align = "Left",
        };
        var frame = TryxPanoramaProtocol.BuildConfigPreset(100, "Pre-set 1: Cooling delivery", overlay);
        var json = ExtractJson(frame);
        // Must parse without throwing - escaping is correct.
        var doc = JsonDocument.Parse(json);
        var display = doc.RootElement.GetProperty("waterBlockScreen").GetProperty("id").GetProperty("sysinfoDisplay");
        Assert.Equal("CPU \"Package\"", display[0].GetString());
        Assert.Equal("Temp\\C", display[1].GetString());
    }
}
