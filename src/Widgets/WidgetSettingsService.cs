using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Persistence;

namespace Nexus.Service.Widgets;

/// <summary>
/// Reads + writes per-instance marketplace widget settings via the layout
/// stored on <see cref="IConfigStore"/>. Each placement has its own
/// <see cref="PanelWidgetDto.Config"/> dictionary; manifest defaults are
/// merged on read so widget code sees a complete document. Type-scoped
/// storage (the old <c>NexusSettings.Widgets</c> bag) was retired in schema v4.
/// </summary>
public sealed class WidgetSettingsService
{
    // Convention for marketplace widget placement types: native widget types
    // are bare ("clock", "monitoring", ...); marketplace placements prefix
    // with this so the renderer can route to the declarative engine instead
    // of a built-in component.
    public const string MarketplaceTypePrefix = "marketplace:";

    private readonly IConfigStore _store;
    private readonly AppRegistry _registry;

    public WidgetSettingsService(IConfigStore store, AppRegistry registry)
    {
        _store = store;
        _registry = registry;
    }

    /// <summary>
    /// Build the effective settings document for a single placement. Manifest
    /// defaults supply the full key set; the placement's persisted overrides
    /// (if any) win on key collision. Returns an empty document if the
    /// placement isn't found or its type doesn't resolve to a known
    /// marketplace widget.
    /// </summary>
    public WidgetSettingsDocument Get(string instanceId)
    {
        var doc = new WidgetSettingsDocument { AppId = instanceId };
        if (!TryResolve(_store.Load(), instanceId, out var widget, out var manifest))
        {
            return doc;
        }
        foreach (var entry in manifest!.Settings)
        {
            doc.Values[entry.Key] = Clone(entry.Default);
        }
        if (widget!.Config is { } overrides)
        {
            foreach (var kv in overrides)
            {
                doc.Values[kv.Key] = kv.Value.Clone();
            }
        }
        return doc;
    }

    /// <summary>
    /// Apply a partial patch to a placement's per-instance config and return
    /// the new effective document. Unknown keys (not declared in the
    /// manifest) are dropped silently.
    /// </summary>
    public WidgetSettingsDocument Apply(string instanceId, WidgetSettingsPatch patch)
    {
        _store.Update(s =>
        {
            if (!TryResolve(s, instanceId, out var widget, out var manifest)) return;
            widget!.Config ??= new Dictionary<string, JsonElement>();
            if (patch.Set is not null)
            {
                foreach (var kv in patch.Set)
                {
                    if (!HasManifestKey(manifest!, kv.Key)) continue;
                    widget.Config[kv.Key] = kv.Value.Clone();
                }
            }
            if (patch.Reset is not null)
            {
                foreach (var key in patch.Reset)
                {
                    widget.Config.Remove(key);
                }
            }
            if (widget.Config.Count == 0) widget.Config = null;
        });
        return Get(instanceId);
    }

    /// <summary>
    /// Find the <see cref="PanelWidgetDto"/> with this id anywhere in the
    /// persisted layouts (active desktop dashboard + every registered panel
    /// device). Also resolves the marketplace manifest so callers don't need
    /// a second lookup. Returns false if either step fails.
    /// </summary>
    private bool TryResolve(NexusSettings s, string instanceId, out PanelWidgetDto? widget, out AppManifest? manifest)
    {
        widget = FindWidget(s, instanceId);
        manifest = null;
        if (widget is null) return false;
        var marketplaceId = MarketplaceIdFromType(widget.Type);
        if (marketplaceId is null) return false;
        if (!_registry.TryGet(marketplaceId, out var entry)) return false;
        manifest = entry.Manifest;
        return true;
    }

    private static PanelWidgetDto? FindWidget(NexusSettings s, string instanceId)
    {
        var dash = s.Panel.DashboardLayout;
        if (dash is not null)
        {
            var hit = FindInLayout(dash, instanceId);
            if (hit is not null) return hit;
        }
        foreach (var device in s.PanelDevices.Values)
        {
            if (device.Layout is null) continue;
            var hit = FindInLayout(device.Layout, instanceId);
            if (hit is not null) return hit;
        }
        return null;
    }

    private static PanelWidgetDto? FindInLayout(PanelLayoutDto layout, string instanceId)
    {
        foreach (var page in layout.Pages)
        {
            foreach (var w in page.Widgets)
            {
                if (w.Id == instanceId) return w;
            }
        }
        return null;
    }

    public static string? MarketplaceIdFromType(string type)
    {
        if (string.IsNullOrEmpty(type)) return null;
        return type.StartsWith(MarketplaceTypePrefix, System.StringComparison.Ordinal)
            ? type.Substring(MarketplaceTypePrefix.Length)
            : null;
    }

    private static bool HasManifestKey(AppManifest manifest, string key)
    {
        foreach (var entry in manifest.Settings)
        {
            if (entry.Key == key) return true;
        }
        return false;
    }

    private static JsonElement Clone(JsonElement? source)
    {
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
