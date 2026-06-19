using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nexus.Service.Lighting.Smart.Drivers.Hue;

// Wire shapes for the Philips Hue APIs. Property names are explicit because Hue
// uses lowercase / snake_case ("internalipaddress", "id_v1", "color_temperature")
// that no single naming policy matches.

// --- Cloud discovery (https://discovery.meethue.com) ---
public sealed class HueDiscoveryEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("internalipaddress")] public string InternalIpAddress { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
}

// --- Legacy /api/0/config (unauthenticated reachability + bridge id) ---
public sealed class HueBridgeConfig
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("bridgeid")] public string BridgeId { get; set; } = "";
    [JsonPropertyName("swversion")] public string SwVersion { get; set; } = "";
    [JsonPropertyName("modelid")] public string ModelId { get; set; } = "";
}

// --- Pairing (POST /api) returns an array of success|error items ---
public sealed class HuePairBody
{
    [JsonPropertyName("devicetype")] public string DeviceType { get; set; } = "";
    [JsonPropertyName("generateclientkey")] public bool GenerateClientKey { get; set; } = true;
}

public sealed class HueApiItem
{
    [JsonPropertyName("success")] public HuePairSuccess? Success { get; set; }
    [JsonPropertyName("error")] public HueError? Error { get; set; }
}

public sealed class HuePairSuccess
{
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("clientkey")] public string ClientKey { get; set; } = "";
}

public sealed class HueError
{
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("description")] public string Description { get; set; } = "";
}

// --- CLIP v2 light list (GET /clip/v2/resource/light) ---
public sealed class HueV2LightResponse
{
    [JsonPropertyName("data")] public List<HueLight> Data { get; set; } = new();
}

public sealed class HueLight
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("id_v1")] public string IdV1 { get; set; } = "";
    [JsonPropertyName("owner")] public HueResourceRef? Owner { get; set; }
    [JsonPropertyName("metadata")] public HueMetadata? Metadata { get; set; }
    [JsonPropertyName("on")] public HueOn? On { get; set; }
    [JsonPropertyName("dimming")] public HueDimming? Dimming { get; set; }
    [JsonPropertyName("color")] public HueColor? Color { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
}

public sealed class HueResourceRef
{
    [JsonPropertyName("rid")] public string Rid { get; set; } = "";
    [JsonPropertyName("rtype")] public string Rtype { get; set; } = "";
}

// --- Entertainment (CLIP v2) ---
public sealed class HueEntConfigResponse
{
    [JsonPropertyName("data")] public List<HueEntConfig> Data { get; set; } = new();
}

public sealed class HueEntConfig
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("metadata")] public HueMetadata? Metadata { get; set; }
    [JsonPropertyName("channels")] public List<HueEntChannel> Channels { get; set; } = new();
}

public sealed class HueEntChannel
{
    [JsonPropertyName("channel_id")] public int ChannelId { get; set; }
    [JsonPropertyName("members")] public List<HueEntMember> Members { get; set; } = new();
}

public sealed class HueEntMember
{
    [JsonPropertyName("service")] public HueResourceRef? Service { get; set; }
}

public sealed class HueEntServiceResponse
{
    [JsonPropertyName("data")] public List<HueEntService> Data { get; set; } = new();
}

public sealed class HueEntService
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("owner")] public HueResourceRef? Owner { get; set; }
}

// PUT body to start/stop streaming on an entertainment configuration.
public sealed class HueEntAction
{
    [JsonPropertyName("action")] public string Action { get; set; } = "start";
}

public sealed class HueMetadata
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("archetype")] public string Archetype { get; set; } = "";
}

public sealed class HueOn
{
    [JsonPropertyName("on")] public bool On { get; set; }
}

public sealed class HueDimming
{
    [JsonPropertyName("brightness")] public double Brightness { get; set; }
}

public sealed class HueColor
{
    [JsonPropertyName("xy")] public HueXy? Xy { get; set; }
}

public sealed class HueXy
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
}

// --- CLIP v2 light update (PUT /clip/v2/resource/light/{id}) ---
public sealed class HueLightUpdate
{
    [JsonPropertyName("on")] public HueOn? On { get; set; }
    [JsonPropertyName("dimming")] public HueDimming? Dimming { get; set; }
    [JsonPropertyName("color")] public HueColor? Color { get; set; }
    [JsonPropertyName("dynamics")] public HueDynamics? Dynamics { get; set; }
}

// Transition control. duration=0 snaps instantly; omitting it lets the lamp
// apply its default ~400ms fade, which smears fast effect updates.
public sealed class HueDynamics
{
    [JsonPropertyName("duration")] public int Duration { get; set; }
}

// Per-light opaque payload we persist in SmartLightConfig.Extra. Carries the v2
// light resource id (for REST control) plus the bridge clientkey (the DTLS PSK
// for Entertainment streaming, captured at pairing). Legacy entries stored just
// the bare rid string - handled on parse.
public sealed class HueDeviceExtra
{
    [JsonPropertyName("rid")] public string Rid { get; set; } = "";
    [JsonPropertyName("ck")] public string ClientKey { get; set; } = "";
}

public sealed class HueIdentifyUpdate
{
    [JsonPropertyName("identify")] public HueIdentifyAction Identify { get; set; } = new();
}

public sealed class HueIdentifyAction
{
    [JsonPropertyName("action")] public string Action { get; set; } = "identify";
}
