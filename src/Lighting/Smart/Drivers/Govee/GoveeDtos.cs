using System.Text.Json.Serialization;

namespace Nexus.Service.Lighting.Smart.Drivers.Govee;

// Wire types for the Govee LAN API. Every message (request and reply) is JSON
// in a {"msg":{"cmd":...,"data":{...}}} envelope over UDP: scan requests to
// multicast :4001, control to the device's :4003, every device reply arrives
// at the client's fixed :4002 listener.

public sealed class GoveeEnvelope<TData>
{
    [JsonPropertyName("msg")] public GoveeMsg<TData>? Msg { get; set; }
}

public sealed class GoveeMsg<TData>
{
    [JsonPropertyName("cmd")] public string Cmd { get; set; } = "";
    [JsonPropertyName("data")] public TData? Data { get; set; }
}

/// <summary>Scan request data - the API requires the literal "reserve".</summary>
public sealed class GoveeScanRequestData
{
    [JsonPropertyName("account_topic")] public string AccountTopic { get; set; } = "reserve";
}

/// <summary>"turn" (0/1) and "brightness" (1-100) request data.</summary>
public sealed class GoveeValueData
{
    [JsonPropertyName("value")] public int Value { get; set; }
}

public sealed class GoveeColor
{
    [JsonPropertyName("r")] public int R { get; set; }
    [JsonPropertyName("g")] public int G { get; set; }
    [JsonPropertyName("b")] public int B { get; set; }
}

/// <summary>"colorwc" request data. Kelvin 0 = render the RGB triple;
/// 2000-9000 = render that white point and ignore RGB.</summary>
public sealed class GoveeColorWcData
{
    [JsonPropertyName("color")] public GoveeColor Color { get; set; } = new();
    [JsonPropertyName("colorTemInKelvin")] public int ColorTemInKelvin { get; set; }
}

/// <summary>"devStatus" request data (must serialize as an empty object).</summary>
public sealed class GoveeEmptyData;

/// <summary>"razer" request data - base64 of the binary realtime packet.</summary>
public sealed class GoveePtData
{
    [JsonPropertyName("pt")] public string Pt { get; set; } = "";
}

/// <summary>Union of every reply's fields ("scan" and "devStatus" don't
/// collide). Replies carry no request correlation - match on source IP.</summary>
public sealed class GoveeReplyData
{
    // scan
    [JsonPropertyName("ip")] public string? Ip { get; set; }
    [JsonPropertyName("device")] public string? Device { get; set; }
    [JsonPropertyName("sku")] public string? Sku { get; set; }
    [JsonPropertyName("bleVersionHard")] public string? BleVersionHard { get; set; }
    [JsonPropertyName("bleVersionSoft")] public string? BleVersionSoft { get; set; }
    [JsonPropertyName("wifiVersionHard")] public string? WifiVersionHard { get; set; }
    [JsonPropertyName("wifiVersionSoft")] public string? WifiVersionSoft { get; set; }
    // devStatus
    [JsonPropertyName("onOff")] public int? OnOff { get; set; }
    [JsonPropertyName("brightness")] public int? Brightness { get; set; }
    [JsonPropertyName("color")] public GoveeColor? Color { get; set; }
    [JsonPropertyName("colorTemInKelvin")] public int? ColorTemInKelvin { get; set; }
}

/// <summary>A device that answered a scan.</summary>
public sealed record GoveeDeviceInfo(string Ip, string Device, string Sku);

/// <summary>Brand payload persisted in <c>SmartLightConfig.Extra</c>:
/// model + realtime capability captured at pair time.</summary>
public sealed class GoveeExtra
{
    [JsonPropertyName("sku")] public string Sku { get; set; } = "";
    /// <summary>Color count for razer frames (per-IC segments).</summary>
    [JsonPropertyName("segments")] public int Segments { get; set; }
    /// <summary>Device accepts razer/DreamView realtime packets.</summary>
    [JsonPropertyName("razer")] public bool Razer { get; set; }
}
