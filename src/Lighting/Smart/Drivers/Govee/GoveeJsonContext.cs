using System.Text.Json.Serialization;

namespace Nexus.Service.Lighting.Smart.Drivers.Govee;

// AOT source-gen surface for the Govee LAN wire types (closed generic
// envelopes, one per command payload shape).
[JsonSerializable(typeof(GoveeEnvelope<GoveeScanRequestData>))]
[JsonSerializable(typeof(GoveeEnvelope<GoveeValueData>))]
[JsonSerializable(typeof(GoveeEnvelope<GoveeColorWcData>))]
[JsonSerializable(typeof(GoveeEnvelope<GoveeEmptyData>))]
[JsonSerializable(typeof(GoveeEnvelope<GoveePtData>))]
[JsonSerializable(typeof(GoveeEnvelope<GoveeReplyData>))]
[JsonSerializable(typeof(GoveeExtra))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class GoveeJsonContext : JsonSerializerContext;
