using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nexus.Service.Lighting.Smart.Drivers.Nanoleaf;

// Wire types for the Nanoleaf Open API (plain HTTP, /api/v1/<token>/...).
// Shared by panel products (Light Panels/Canvas/Shapes/Elements/Lines) and the
// Matter WiFi Essentials line (same endpoints, no panelLayout, GET /length).

public sealed class NanoleafAuthResponse
{
    [JsonPropertyName("auth_token")] public string AuthToken { get; set; } = "";
}

public sealed class NanoleafInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("serialNo")] public string SerialNo { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("firmwareVersion")] public string FirmwareVersion { get; set; } = "";
    [JsonPropertyName("panelLayout")] public NanoleafPanelLayout? PanelLayout { get; set; }
}

public sealed class NanoleafPanelLayout
{
    [JsonPropertyName("layout")] public NanoleafLayout? Layout { get; set; }
}

public sealed class NanoleafLayout
{
    [JsonPropertyName("numPanels")] public int NumPanels { get; set; }
    [JsonPropertyName("sideLength")] public int SideLength { get; set; }
    [JsonPropertyName("positionData")] public List<NanoleafPanelPosition> PositionData { get; set; } = new();
}

public sealed class NanoleafPanelPosition
{
    [JsonPropertyName("panelId")] public int PanelId { get; set; }
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("o")] public int O { get; set; }
    [JsonPropertyName("shapeType")] public int ShapeType { get; set; }
}

/// <summary>Essentials-only: GET /length → LED count of the strip/bulb.</summary>
public sealed class NanoleafLengthResponse
{
    [JsonPropertyName("numLEDs")] public int NumLeds { get; set; }
}

public sealed class NanoleafBoolValue
{
    [JsonPropertyName("value")] public bool Value { get; set; }
}

public sealed class NanoleafIntValue
{
    [JsonPropertyName("value")] public int Value { get; set; }
}

/// <summary>PUT /state body. The device is order-sensitive when combining keys:
/// "on" must serialize last or a combined on+brightness write can be ignored.</summary>
public sealed class NanoleafStateWrite
{
    [JsonPropertyName("brightness"), JsonPropertyOrder(0)] public NanoleafIntValue? Brightness { get; set; }
    [JsonPropertyName("hue"), JsonPropertyOrder(1)] public NanoleafIntValue? Hue { get; set; }
    [JsonPropertyName("sat"), JsonPropertyOrder(2)] public NanoleafIntValue? Sat { get; set; }
    [JsonPropertyName("ct"), JsonPropertyOrder(3)] public NanoleafIntValue? Ct { get; set; }
    [JsonPropertyName("on"), JsonPropertyOrder(10)] public NanoleafBoolValue? On { get; set; }
}

public sealed class NanoleafEffectsWrite
{
    [JsonPropertyName("write")] public NanoleafWriteCommand? Write { get; set; }
}

public sealed class NanoleafWriteCommand
{
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("animType")] public string? AnimType { get; set; }
    [JsonPropertyName("extControlVersion")] public string? ExtControlVersion { get; set; }
}

/// <summary>Brand payload persisted in <c>SmartLightConfig.Extra</c>. Captured
/// at pair time: addressing mode, zone ids, normalized panel-centroid UVs, and
/// the ports (REST + extControl stream) the device was paired on.</summary>
public sealed class NanoleafExtra
{
    public const string KindPanels = "panels";
    public const string KindLeds = "leds";

    [JsonPropertyName("port")] public int Port { get; set; } = NanoleafDriver.DefaultRestPort;
    [JsonPropertyName("streamPort")] public int StreamPort { get; set; } = NanoleafDriver.DefaultStreamPort;
    /// <summary>"panels" = zone per panelId (panel products); "leds" = zone per
    /// LED index 0..N-1 (Essentials strips/bulbs).</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = KindPanels;
    [JsonPropertyName("panelIds")] public int[] PanelIds { get; set; } = Array.Empty<int>();
    [JsonPropertyName("u")] public float[]? U { get; set; }
    [JsonPropertyName("v")] public float[]? V { get; set; }
    [JsonPropertyName("ledCount")] public int LedCount { get; set; }
}
