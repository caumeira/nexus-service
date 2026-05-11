using System.Text.Json.Serialization;
using Qos.Service.Models.Profiles;
using Qos.Service.Persistence;

namespace Qos.Service.Serialization;

[JsonSerializable(typeof(QosSettings))]
[JsonSerializable(typeof(AuthSettings))]
[JsonSerializable(typeof(ObsSettings))]
[JsonSerializable(typeof(SteamSettings))]
[JsonSerializable(typeof(DiscordSettings))]
[JsonSerializable(typeof(PanelPhoneSessionToken))]
[JsonSerializable(typeof(List<PanelPhoneSessionToken>))]
[JsonSerializable(typeof(LedPositionOverride))]
[JsonSerializable(typeof(List<LedPositionOverride>))]
[JsonSerializable(typeof(Dictionary<string, List<LedPositionOverride>>))]
[JsonSerializable(typeof(ProfileManifest))]
[JsonSerializable(typeof(ProfileExport))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
public partial class PersistenceJsonContext : JsonSerializerContext;
