using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nexus.Service.Lighting.Smart.Drivers.Hue;

// AOT source-gen surface for the Hue wire types. No naming policy - every
// property carries an explicit [JsonPropertyName] (Hue's casing is irregular).
// Null fields are omitted so a partial PUT (color-only / on-only) is valid.
[JsonSerializable(typeof(HueDiscoveryEntry))]
[JsonSerializable(typeof(List<HueDiscoveryEntry>))]
[JsonSerializable(typeof(HueBridgeConfig))]
[JsonSerializable(typeof(HuePairBody))]
[JsonSerializable(typeof(HueApiItem))]
[JsonSerializable(typeof(List<HueApiItem>))]
[JsonSerializable(typeof(HuePairSuccess))]
[JsonSerializable(typeof(HueError))]
[JsonSerializable(typeof(HueV2LightResponse))]
[JsonSerializable(typeof(HueLight))]
[JsonSerializable(typeof(List<HueLight>))]
[JsonSerializable(typeof(HueMetadata))]
[JsonSerializable(typeof(HueOn))]
[JsonSerializable(typeof(HueDimming))]
[JsonSerializable(typeof(HueColor))]
[JsonSerializable(typeof(HueXy))]
[JsonSerializable(typeof(HueLightUpdate))]
[JsonSerializable(typeof(HueDynamics))]
[JsonSerializable(typeof(HueDeviceExtra))]
[JsonSerializable(typeof(HueResourceRef))]
[JsonSerializable(typeof(HueEntConfigResponse))]
[JsonSerializable(typeof(HueEntConfig))]
[JsonSerializable(typeof(List<HueEntConfig>))]
[JsonSerializable(typeof(HueEntChannel))]
[JsonSerializable(typeof(List<HueEntChannel>))]
[JsonSerializable(typeof(HueEntMember))]
[JsonSerializable(typeof(HueEntServiceResponse))]
[JsonSerializable(typeof(HueEntService))]
[JsonSerializable(typeof(List<HueEntService>))]
[JsonSerializable(typeof(HueEntAction))]
[JsonSerializable(typeof(HueIdentifyUpdate))]
[JsonSerializable(typeof(HueIdentifyAction))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class HueJsonContext : JsonSerializerContext;
