using System.Collections.Generic;
using System.Text.Json;
using Qos.Service.Models.Widgets;
using Qos.Service.Persistence;

namespace Qos.Service.Widgets;

/// <summary>
/// Reads + writes the persisted per-widget setting overrides via the
/// existing <see cref="IConfigStore"/>. Pure facade: storage lives in
/// <see cref="QosSettings.Widgets"/>, the document store handles atomic
/// write + change events.
/// </summary>
/// <remarks>
/// Stored values are JSON-encoded strings (not <see cref="JsonElement"/>)
/// because the AOT JSON source generator is brittle around mixed-type
/// dictionaries. The read path parses them back into <see cref="JsonElement"/>
/// at the API boundary so the wire shape stays clean.
/// </remarks>
public sealed class WidgetSettingsService
{
    private readonly IConfigStore _store;

    public WidgetSettingsService(IConfigStore store) { _store = store; }

    /// <summary>
    /// Merge manifest defaults with persisted overrides. Manifest defaults
    /// supply the full key set; overrides win on key collision.
    /// </summary>
    public WidgetSettingsDocument Get(string widgetId, WidgetManifest manifest)
    {
        var doc = new WidgetSettingsDocument { WidgetId = widgetId };
        foreach (var entry in manifest.Settings)
        {
            // Default may be the literal `default` value in the manifest, an
            // undefined element when omitted, or null - mirror whichever as-is.
            doc.Values[entry.Key] = Clone(entry.Default);
        }
        if (_store.Load().Widgets.TryGetValue(widgetId, out var overrides))
        {
            foreach (var kv in overrides)
            {
                doc.Values[kv.Key] = ParseStoredValue(kv.Value);
            }
        }
        return doc;
    }

    /// <summary>
    /// Apply a partial patch. Returns the new effective document so the
    /// caller can echo it to the widget without a second round trip.
    /// </summary>
    public WidgetSettingsDocument Apply(string widgetId, WidgetManifest manifest, WidgetSettingsPatch patch)
    {
        _store.Update(s =>
        {
            if (!s.Widgets.TryGetValue(widgetId, out var bag))
            {
                bag = new Dictionary<string, string>();
                // Don't insert the bag yet - a reset-only or empty patch
                // leaves it empty; we only want to add it when there's
                // something to persist (avoids growing settings.json with
                // hollow per-widget entries over time).
            }
            // Set first, Reset second - documented in WidgetSettingsPatch.
            if (patch.Set is not null)
            {
                foreach (var kv in patch.Set)
                {
                    if (!HasManifestKey(manifest, kv.Key)) continue; // drop unknown keys
                    bag[kv.Key] = SerialiseStored(kv.Value);
                }
            }
            if (patch.Reset is not null)
            {
                foreach (var key in patch.Reset)
                {
                    bag.Remove(key);
                }
            }
            if (bag.Count > 0)
            {
                s.Widgets[widgetId] = bag;
            }
            else
            {
                s.Widgets.Remove(widgetId);
            }
        });
        return Get(widgetId, manifest);
    }

    private static bool HasManifestKey(WidgetManifest manifest, string key)
    {
        foreach (var entry in manifest.Settings)
        {
            if (entry.Key == key) return true;
        }
        return false;
    }

    private static string SerialiseStored(JsonElement value)
    {
        // Store the canonical wire-text form so reads round-trip stably.
        return value.GetRawText();
    }

    private static JsonElement ParseStoredValue(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Corrupt entry - fall back to a JSON null so the widget sees a
            // well-typed value rather than a parse exception.
            return JsonElementNull();
        }
    }

    private static JsonElement Clone(JsonElement? source)
    {
        // JsonElement is a struct over a backing JsonDocument; Clone copies the
        // node into a standalone document so it survives the parent doc going
        // out of scope (e.g. when reading manifests).
        if (source is null) return JsonElementNull();
        if (source.Value.ValueKind == JsonValueKind.Undefined) return JsonElementNull();
        return source.Value.Clone();
    }

    private static JsonElement JsonElementNull()
    {
        using var doc = JsonDocument.Parse("null");
        return doc.RootElement.Clone();
    }
}
