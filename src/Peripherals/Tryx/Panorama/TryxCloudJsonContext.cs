using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

// AOT source-gen surface for the Tryx cloud (Kanali) wallpaper catalog API.
// Every request/response body is itself carried as a JSON string scalar (the
// SM2 ciphertext hex), so the plain `string` type is registered alongside the
// wire DTOs - see TryxCloudCatalog.cs for why.
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(TryxMaterialQueryRequest))]
[JsonSerializable(typeof(TryxMaterialGetRequest))]
[JsonSerializable(typeof(TryxMaterialGetUrlRequest))]
[JsonSerializable(typeof(TryxMaterialRecord))]
[JsonSerializable(typeof(List<TryxMaterialRecord>))]
[JsonSerializable(typeof(TryxHardwareInfoEntry))]
[JsonSerializable(typeof(List<TryxHardwareInfoEntry>))]
[JsonSerializable(typeof(List<int>))]
[JsonSerializable(typeof(TryxCloudEnvelope<List<int>>))]
[JsonSerializable(typeof(TryxCloudEnvelope<List<TryxMaterialRecord>>))]
[JsonSerializable(typeof(TryxCloudEnvelope<string>))]
public partial class TryxCloudJsonContext : JsonSerializerContext;
