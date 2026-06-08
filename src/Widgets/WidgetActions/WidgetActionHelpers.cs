using System.Collections.Generic;
using System.Text.Json;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Serialization;

namespace Nexus.Service.Widgets.WidgetActions;

/// <summary>Shared helpers for host-action handlers: an AOT-safe ack payload
/// and typed argument extraction.</summary>
internal static class WidgetActionHelpers
{
    public static JsonElement Ack(bool ok, string? message = null, string? applied = null)
    {
        var dto = new WidgetActionAckDto { Ok = ok, Message = message, Applied = applied };
        var json = JsonSerializer.Serialize(dto, AppJsonContext.Default.WidgetActionAckDto);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static string? Str(Dictionary<string, JsonElement>? args, string key)
        => args != null && args.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    public static double? Num(Dictionary<string, JsonElement>? args, string key)
        => args != null && args.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : null;
}
