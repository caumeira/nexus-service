using System.Collections.Generic;
using System.Text.Json;

namespace Qos.Service.Models.Widgets;

/// <summary>
/// Effective widget settings document: manifest defaults overlaid with the
/// user's persisted overrides. Values are <see cref="JsonElement"/> so the
/// wire shape preserves whatever the manifest declared (color string, number,
/// boolean, etc.) without a tagged-union schema.
/// </summary>
public sealed class WidgetSettingsDocument
{
    public string WidgetId { get; set; } = "";
    public Dictionary<string, JsonElement> Values { get; set; } = new();
}

/// <summary>
/// Body of <c>PATCH /widgets-api/installed/{id}/settings</c>. Partial: any
/// key in <see cref="Set"/> overwrites; keys in <see cref="Reset"/> drop back
/// to the manifest default; keys absent from both are untouched. When the
/// same key appears in both lists, <see cref="Reset"/> wins (the server
/// applies <c>Set</c> first then <c>Reset</c>).
/// </summary>
public sealed class WidgetSettingsPatch
{
    public Dictionary<string, JsonElement>? Set { get; set; }
    public List<string>? Reset { get; set; }
}
