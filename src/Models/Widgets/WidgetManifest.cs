using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Widgets;

/// <summary>
/// Parsed <c>manifest.json</c> for an installed widget under the declarative
/// schema <c>nexus.widget/2</c>. Field names match the on-disk JSON. See
/// <c>plans/widget-sdk.md</c> for the authoritative contract.
/// </summary>
public sealed class WidgetManifest
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("author")]
    public WidgetManifestAuthor? Author { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("min_nexus_version")]
    public string MinNexusVersion { get; set; } = "";

    [JsonPropertyName("surfaces")]
    public List<string> Surfaces { get; set; } = new();

    /// <summary>
    /// Allowed grid sizes. Same alphabet as the panel engine:
    /// <c>1x1</c>, <c>2x2</c>, <c>4x2</c>, <c>4x4</c>. The first entry is
    /// the default if <see cref="DefaultSize"/> is unset. Anything outside
    /// the alphabet is dropped by the panel registry's marketplace filter.
    /// </summary>
    [JsonPropertyName("sizes")]
    public List<string> Sizes { get; set; } = new();

    [JsonPropertyName("default_size")]
    public string? DefaultSize { get; set; }

    [JsonPropertyName("viewport")]
    public WidgetManifestViewport? Viewport { get; set; }

    [JsonPropertyName("capabilities")]
    public WidgetManifestCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("settings")]
    public List<WidgetManifestSettingEntry> Settings { get; set; } = new();

    /// <summary>
    /// Data sources the view tree binds to. Keyed by binding name. Each
    /// source is either a sensor reference, a REST fetch with JSONPath
    /// extraction, or a passthrough from the optional Tier 2 worker.
    /// </summary>
    [JsonPropertyName("data")]
    public Dictionary<string, WidgetManifestDataSource> Data { get; set; } = new();

    /// <summary>
    /// View tree. Either a single view (rendered at every supported size)
    /// or a per-size map (<c>{ "2x2": ..., "4x2": ..., "4x4": ... }</c>).
    /// Stored as a <see cref="JsonElement"/> so the AOT source generator
    /// can carry it through without committing to a closed schema; the
    /// renderer interprets it at runtime against the meter palette.
    /// </summary>
    [JsonPropertyName("view")]
    public JsonElement View { get; set; }

    /// <summary>
    /// Author-bundled font faces. The host loads them via the FontFace
    /// API and scopes the family name to the widget id, so two widgets
    /// shipping fonts named "led" don't collide. Referenced from the
    /// view tree via the meter's <c>font</c> prop.
    /// </summary>
    [JsonPropertyName("fonts")]
    public List<WidgetManifestFont> Fonts { get; set; } = new();

    /// <summary>
    /// Default values for the widget's per-instance local state bag.
    /// Bindings read via <c>{local.*}</c>; <c>button.onClick.localUpdate</c>
    /// mutates. Persisted to localStorage keyed by widgetId + instanceId.
    /// Pass-through JsonElement (open-shape) so authors can put anything
    /// JSON-serialisable in there. Nullable because <c>JsonElement</c>
    /// defaults to <c>ValueKind.Undefined</c> which is not valid JSON;
    /// widgets without a <c>local</c> block surface as <c>null</c> on the
    /// wire rather than throwing during response serialisation.
    /// </summary>
    [JsonPropertyName("local")]
    public JsonElement? Local { get; set; }
}

public sealed class WidgetManifestFont
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("src")] public string Src { get; set; } = "";
    [JsonPropertyName("weight")] public string? Weight { get; set; }
    [JsonPropertyName("style")] public string? Style { get; set; }
}

public sealed class WidgetManifestAuthor
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
}

public sealed class WidgetManifestViewport
{
    [JsonPropertyName("min")] public List<int>? Min { get; set; }
    [JsonPropertyName("preferred")] public List<int>? Preferred { get; set; }
    [JsonPropertyName("max")] public List<int>? Max { get; set; }
    [JsonPropertyName("aspect")] public string? Aspect { get; set; }
}

public sealed class WidgetManifestCapabilities
{
    [JsonPropertyName("sensors.read")]
    public List<string> SensorsRead { get; set; } = new();

    [JsonPropertyName("rgb.read")]
    public bool RgbRead { get; set; }

    [JsonPropertyName("rgb.write")]
    public bool RgbWrite { get; set; }

    /// <summary>HTTPS hosts the widget may fetch from. Phase 2 uses the
    /// host-mediated proxy (`/widgets-api/proxy`) for both Tier 1 declarative
    /// fetch sources and Tier 2 worker `nexus.net.fetch` calls.</summary>
    [JsonPropertyName("net.fetch")]
    public List<string> NetFetch { get; set; } = new();

    /// <summary>
    /// Host-action allowlist. Widgets may POST to /widgets-api/dispatch
    /// only with action names that appear in this list. Names are
    /// dotted (e.g. "displays.list", "displays.setBrightness"); the
    /// server-side registry knows which controller each routes to.
    /// </summary>
    [JsonPropertyName("dispatch")]
    public List<string> Dispatch { get; set; } = new();

    [JsonPropertyName("config")]
    public bool Config { get; set; } = true;

    /// <summary>
    /// Opt-in Tier 2 capability. When <c>true</c>, the bundle must ship
    /// a <c>worker.js</c> alongside the manifest; the host spawns a Web
    /// Worker per widget instance and exposes the <c>nexus.*</c> API there.
    /// Stored as the JSON discriminator string (currently only "worker").
    /// </summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>Convenience: true when <see cref="Code"/> is "worker".</summary>
    [JsonIgnore]
    public bool WorkerCode => string.Equals(Code, "worker", System.StringComparison.Ordinal);
}

public sealed class WidgetManifestSettingEntry
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("default")] public JsonElement? Default { get; set; }
    [JsonPropertyName("min")] public double? Min { get; set; }
    [JsonPropertyName("max")] public double? Max { get; set; }
    [JsonPropertyName("step")] public double? Step { get; set; }
    [JsonPropertyName("filter")] public string? Filter { get; set; }
    [JsonPropertyName("options")] public List<string>? Options { get; set; }
}

/// <summary>
/// A single binding source. Exactly one of <see cref="Sensor"/>, <see cref="Fetch"/>,
/// <see cref="Worker"/>, <see cref="Clock"/>, or <see cref="Host"/> should be set.
/// The renderer picks based on which is present.
/// </summary>
public sealed class WidgetManifestDataSource
{
    [JsonPropertyName("sensor")]
    public string? Sensor { get; set; }

    [JsonPropertyName("fetch")]
    public string? Fetch { get; set; }

    [JsonPropertyName("refresh")]
    public string? Refresh { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("extract")]
    public Dictionary<string, string>? Extract { get; set; }

    /// <summary>
    /// When set, this binding receives values published by the bundle's
    /// worker.js via <c>nexus.publish</c>. The string value is the
    /// publish-payload key. Mutually exclusive with sensor/fetch.
    /// </summary>
    [JsonPropertyName("worker")]
    public string? Worker { get; set; }

    /// <summary>
    /// Host-ticking clock. The renderer polls <see cref="WidgetClockSource.TickEvery"/>
    /// (default 1s) and surfaces formatted time parts under this binding's name.
    /// Used by the clock widget and by stateful widgets (stopwatch/timer) that
    /// need a re-render heartbeat.
    /// </summary>
    [JsonPropertyName("clock")]
    public WidgetClockSource? Clock { get; set; }

    /// <summary>
    /// Host-action source. The renderer POSTs to /widgets-api/dispatch with
    /// the named action on a schedule (<see cref="WidgetHostSource.Refresh"/>,
    /// default 5s) and surfaces the result under this binding's name. The
    /// action must appear in the manifest's <c>capabilities.dispatch</c>
    /// allowlist.
    /// </summary>
    [JsonPropertyName("host")]
    public WidgetHostSource? Host { get; set; }
}

/// <summary>Configuration for a <c>clock</c> data source.</summary>
public sealed class WidgetClockSource
{
    /// <summary>Tick cadence, e.g. <c>"1s"</c>, <c>"500ms"</c>, <c>"100ms"</c>,
    /// <c>"1m"</c>. The renderer enforces a 100 ms floor.</summary>
    [JsonPropertyName("tickEvery")]
    public string? TickEvery { get; set; }

    /// <summary>IANA timezone (e.g. <c>"America/New_York"</c>). Empty / null
    /// means the system zone. May itself be a <c>{settings.x}</c> binding.</summary>
    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    [JsonPropertyName("hour12")]
    public bool? Hour12 { get; set; }
}

/// <summary>Configuration for a <c>host</c> data source.</summary>
public sealed class WidgetHostSource
{
    /// <summary>Dotted action name routed through the dispatch registry
    /// (e.g. <c>"screentime.today"</c>).</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    /// <summary>Refresh cadence, e.g. <c>"5s"</c>, <c>"30s"</c>, <c>"1m"</c>.
    /// Defaults to 5 s in the renderer.</summary>
    [JsonPropertyName("refresh")]
    public string? Refresh { get; set; }

    /// <summary>Open-shape args passed to the action handler. Pass-through
    /// JsonElement so authors can put anything JSON-serialisable in there.
    /// Nullable for the same reason as <c>WidgetManifest.Local</c>.</summary>
    [JsonPropertyName("args")]
    public JsonElement? Args { get; set; }
}
