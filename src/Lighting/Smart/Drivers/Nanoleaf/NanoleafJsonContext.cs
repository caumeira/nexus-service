using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nexus.Service.Lighting.Smart.Drivers.Nanoleaf;

// AOT source-gen surface for the Nanoleaf wire types. Explicit
// [JsonPropertyName] everywhere (the API mixes camelCase and snake_case).
// Nulls omitted so a partial state PUT (brightness-only etc.) stays valid.
[JsonSerializable(typeof(NanoleafAuthResponse))]
[JsonSerializable(typeof(NanoleafInfo))]
[JsonSerializable(typeof(NanoleafPanelLayout))]
[JsonSerializable(typeof(NanoleafLayout))]
[JsonSerializable(typeof(NanoleafPanelPosition))]
[JsonSerializable(typeof(List<NanoleafPanelPosition>))]
[JsonSerializable(typeof(NanoleafLengthResponse))]
[JsonSerializable(typeof(NanoleafBoolValue))]
[JsonSerializable(typeof(NanoleafIntValue))]
[JsonSerializable(typeof(NanoleafStateWrite))]
[JsonSerializable(typeof(NanoleafEffectsWrite))]
[JsonSerializable(typeof(NanoleafWriteCommand))]
[JsonSerializable(typeof(NanoleafExtra))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class NanoleafJsonContext : JsonSerializerContext;
