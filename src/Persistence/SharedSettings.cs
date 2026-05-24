using System.Collections.Generic;
using Nexus.Service.Models.Panel;

namespace Nexus.Service.Persistence;

// Shared POCOs used by both install-defaults (the seed table) and the live
// NexusSettings document. install-defaults populates the cosmetic + seed fields
// and leaves runtime-only fields (DashboardLayout, OverlayLayout,
// DetailedCollapsed) null; the live profile populates runtime-only fields and
// usually leaves Layouts null because the install-defaults table remains the
// source of truth for seeding new device records.

public sealed class ThemeSettings
{
    public string Language { get; set; } = "en";
    public string ThemeMode { get; set; } = "system";
    public string AccentColor { get; set; } = "#2563eb";
}

public sealed class MonitoringSettings
{
    public bool ShowAverage { get; set; } = true;
    public bool ShowMacStatusBarIcon { get; set; } = true;
    public bool ShowWindowsTrayIcon { get; set; } = true;
    /// <summary>IDs of sections collapsed on the Monitoring "Detailed" tab. Empty list = every section expanded. The SPA writes the full list on every toggle so the persisted state matches the current UI exactly.</summary>
    public List<string> DetailedCollapsed { get; set; } = new();
}

public sealed class PanelSettings
{
    /// <summary>Runtime visibility of the Y70 panel kiosk. When true, the
    /// nexus-overlay sidecar opens the kiosk window (and auto-relaunches when
    /// the Y70 reconnects). Surfaced as "Show Panel" in the UI.</summary>
    public bool AutoLaunch { get; set; }
    public bool ThemeSyncWithDesktop { get; set; } = true;
    public string ThemeMode { get; set; } = "system";
    public bool AccentSyncWithDesktop { get; set; } = true;
    public string? AccentColor { get; set; }
    public string? BackgroundColor { get; set; }
    public string? BackgroundColorLight { get; set; }
    public string BackgroundMode { get; set; } = "solid";
    public string BackgroundEffect { get; set; } = "aurora";
    public int BackgroundTemplate { get; set; }
    public double BackgroundOpacity { get; set; } = 0.4;
    /// <summary>Opacity of the entire panel surface itself (0 = fully transparent,
    /// desktop wallpaper visible through the kiosk; 1 = fully opaque). Distinct
    /// from BackgroundOpacity which is the dim of the background effect over the
    /// panel's widgets. Surfaced as "Panel Opacity" on monitor-style panels only.</summary>
    public double PanelOpacity { get; set; } = 1.0;
    public double WidgetOpacity { get; set; } = 1.0;
    public bool WidgetLabels { get; set; } = true;
    /// <summary>Layout seeds for new device records + first-time desktop dashboard. Populated in install-defaults; null in the live profile (the embedded install-defaults table remains the source of truth for seeding new device records).</summary>
    public PanelLayoutsDefaults? Layouts { get; set; }
    /// <summary>Active desktop dashboard layout (profile-scoped). Null in install-defaults; null in the live profile means "seed from Layouts.Desktop on first load".</summary>
    public PanelLayoutDto? DashboardLayout { get; set; }
}

public sealed class OverlaySettings
{
    public bool Enabled { get; set; }
    public bool AlwaysOnTop { get; set; }
    public int Scale { get; set; } = 100;
    public double Opacity { get; set; } = 1.0;
    public int Monitor { get; set; } = -1;
    /// <summary>Pinned floating-widget instances. Empty list = no widgets pinned.</summary>
    public List<OverlayWidgetDto> Layout { get; set; } = new();
}

// Layout seed types — used by install-defaults to define starter widget sets
// per surface. Live PanelDeviceRecord.Layout and PanelSettings.DashboardLayout
// use the richer PanelLayoutDto with ids + per-instance widget config.

public sealed class PanelLayoutsDefaults
{
    public PanelLayoutDefault Desktop { get; set; } = new();
    public PanelLayoutDefault Y70 { get; set; } = new();
    public PanelLayoutDefault Phone { get; set; } = new();
    public PanelLayoutDefault Q60 { get; set; } = new();
}

public sealed class PanelLayoutDefault
{
    public int LayoutSchemaVersion { get; set; } = 2;
    public string Surface { get; set; } = "";
    public List<PanelLayoutWidget> Widgets { get; set; } = new();
}

public sealed class PanelLayoutWidget
{
    public string Type { get; set; } = "";
    public string Size { get; set; } = "";
    public int Col { get; set; }
    public int Row { get; set; }
    /// <summary>Optional per-instance seed config copied verbatim into the new widget instance when this seed is materialized. Same shape as <see cref="PanelWidgetDto.Config"/>: keys are widget-defined; values are raw JSON.</summary>
    public Dictionary<string, System.Text.Json.JsonElement>? Config { get; set; }
}

// GET /preferences response. Mirrors PreferencesPatch shape so the SPA reads
// and writes through the same nested keys. CoolingPrefs is a slim projection
// of CoolingSettings (only the prefs surface — the full cooling view has its
// own endpoints).
public sealed class Preferences
{
    public ThemeSettings Theme { get; set; } = new();
    public PanelSettings Panel { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();
    public MonitoringSettings Monitoring { get; set; } = new();
    public CoolingPrefs Cooling { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
}

public sealed class CoolingPrefs
{
    public List<string>? FanChannelOrder { get; set; }
    public string? PreferredCpuTempSensorId { get; set; }
    public string? PreferredGpuTempSensorId { get; set; }
}

// PATCH wrappers. POST /preferences accepts PreferencesPatch with optional
// per-domain sub-patches; each sub-patch's fields are nullable so the handler
// can distinguish "client left this out" from "client explicitly sent value".

public sealed class PreferencesPatch
{
    public ThemeSettingsPatch? Theme { get; set; }
    public PanelSettingsPatch? Panel { get; set; }
    public OverlaySettingsPatch? Overlay { get; set; }
    public MonitoringSettingsPatch? Monitoring { get; set; }
    public CoolingPrefsPatch? Cooling { get; set; }
    public UiSettingsPatch? Ui { get; set; }
}

public sealed class ThemeSettingsPatch
{
    public string? Language { get; set; }
    public string? ThemeMode { get; set; }
    public string? AccentColor { get; set; }
}

public sealed class PanelSettingsPatch
{
    public bool? AutoLaunch { get; set; }
    public bool? ThemeSyncWithDesktop { get; set; }
    public string? ThemeMode { get; set; }
    public bool? AccentSyncWithDesktop { get; set; }
    public string? AccentColor { get; set; }
    public string? BackgroundColor { get; set; }
    public string? BackgroundColorLight { get; set; }
    public string? BackgroundMode { get; set; }
    public string? BackgroundEffect { get; set; }
    public int? BackgroundTemplate { get; set; }
    public double? BackgroundOpacity { get; set; }
    public double? PanelOpacity { get; set; }
    public double? WidgetOpacity { get; set; }
    public bool? WidgetLabels { get; set; }
    public PanelLayoutDto? DashboardLayout { get; set; }
}

public sealed class OverlaySettingsPatch
{
    public bool? Enabled { get; set; }
    public bool? AlwaysOnTop { get; set; }
    public int? Scale { get; set; }
    public double? Opacity { get; set; }
    public int? Monitor { get; set; }
    public List<OverlayWidgetDto>? Layout { get; set; }
}

public sealed class MonitoringSettingsPatch
{
    public bool? ShowAverage { get; set; }
    public bool? ShowMacStatusBarIcon { get; set; }
    public bool? ShowWindowsTrayIcon { get; set; }
    public List<string>? DetailedCollapsed { get; set; }
}

public sealed class CoolingPrefsPatch
{
    public List<string>? FanChannelOrder { get; set; }
    // The two sensor-id fields reuse `string?` for both "field omitted" and
    // "reset to auto" — null on the wire means the client did not send it
    // (handler preserves the stored value); an empty string means the user
    // explicitly cleared their pinned choice (handler stores null). Do not
    // collapse these into a single semantic without updating ProfileRoutes.
    public string? PreferredCpuTempSensorId { get; set; }
    public string? PreferredGpuTempSensorId { get; set; }
}
