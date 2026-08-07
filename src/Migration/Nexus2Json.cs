using System.Text.Json;

namespace Nexus.Service.Migration;

/// <summary>Small tolerant JSON accessors for reading the Nexus 2 config.json
/// DOM: a missing/wrong-kind property returns the fallback instead of
/// throwing, since a malformed section must never fail the whole read.</summary>
internal static class Nexus2Json
{
    public static string? GetString(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    public static double? GetDouble(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var el) &&
        el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var v)
            ? v
            : null;

    public static bool GetBool(JsonElement obj, string prop, bool defaultValue) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? el.ValueKind == JsonValueKind.True
            : defaultValue;

    public static JsonElement? GetObject(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Object
            ? el
            : null;

    public static JsonElement? GetArray(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Array
            ? el
            : null;
}
