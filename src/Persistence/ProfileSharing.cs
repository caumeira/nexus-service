using System;
using System.Collections.Generic;

namespace Qos.Service.Persistence;

/// <summary>
/// Routing helpers for the per-category profile sharing feature. Four categories
/// correspond to the QosSettings sections that the user can pin to a Primary
/// profile (so switching profiles still loads that profile's data for the pinned
/// category). Theme and Dashboard split <see cref="UiSettings"/> at the
/// (Language/ThemeMode/AccentColor) boundary; everything else under Ui that's
/// truly per-profile (monitoring view state, dashboard widget layout, overlay
/// floating widgets) lives under Dashboard. Hardware-bound state - Keeb, Y70,
/// Devices, all Panel* defaults under Ui, system tray / status bar toggles -
/// lives at the QosSettings root and is NEVER profile-scoped, so it has
/// no entry here.
/// </summary>
public static class ProfileSharing
{
    public const string Lighting = "lighting";
    public const string Cooling = "cooling";
    public const string Theme = "theme";
    public const string Dashboard = "dashboard";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Lighting, Cooling, Theme, Dashboard,
    };

    public static string? Normalize(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var lower = id.Trim().ToLowerInvariant();
        for (var i = 0; i < All.Count; i++)
        {
            if (All[i] == lower)
            {
                return All[i];
            }
        }
        return null;
    }

    /// <summary>Copies the named category from <paramref name="source"/> onto <paramref name="target"/>. For Theme/Dashboard, the corresponding subset of Ui fields is copied; the other Ui fields on target are preserved.</summary>
    public static void ApplyCategory(QosSettings target, QosSettings source, string category)
    {
        switch (Normalize(category))
        {
            case Lighting:
                target.Lighting = source.Lighting ?? new LightingSettings();
                break;
            case Cooling:
                target.Cooling = source.Cooling ?? new CoolingSettings();
                break;
            case Theme:
                CopyTheme(source.Ui ?? new UiSettings(), target.Ui ??= new UiSettings());
                break;
            case Dashboard:
                CopyDashboard(source.Ui ?? new UiSettings(), target.Ui ??= new UiSettings());
                break;
        }
    }

    /// <summary>Resets the named category on <paramref name="target"/> to a fresh default value. For Theme/Dashboard the corresponding Ui subset is reset; the other Ui subset is preserved.</summary>
    public static void ResetCategory(QosSettings target, string category)
    {
        switch (Normalize(category))
        {
            case Lighting:
                target.Lighting = new LightingSettings();
                break;
            case Cooling:
                target.Cooling = new CoolingSettings();
                break;
            case Theme:
                CopyTheme(new UiSettings(), target.Ui ??= new UiSettings());
                break;
            case Dashboard:
                CopyDashboard(new UiSettings(), target.Ui ??= new UiSettings());
                break;
        }
    }

    /// <summary>Theme = Language + ThemeMode + AccentColor.</summary>
    private static void CopyTheme(UiSettings source, UiSettings target)
    {
        target.Language = source.Language;
        target.ThemeMode = source.ThemeMode;
        target.AccentColor = source.AccentColor;
    }

    /// <summary>Dashboard = the desktop-side per-profile Ui fields: monitoring view state, fan-channel display order, dashboard widget layout, overlay floating widgets, and the conflict-alert toggle. Panel-related Ui fields (PanelTheme*, PanelAccent*, PanelBackground*, PanelWidgetOpacity, PanelWidgetLabels, PanelAutoLaunch) are workstation-level - they describe how panel devices look and behave, not the active profile - so they are NOT copied here. Same for system tray / status bar toggles, which are OS-level prefs.</summary>
    private static void CopyDashboard(UiSettings source, UiSettings target)
    {
        target.DisableConflictAlerts = source.DisableConflictAlerts;
        target.MonitoringShowAverage = source.MonitoringShowAverage;
        target.MonitoringDetailedCollapsed = source.MonitoringDetailedCollapsed ?? new List<string>();
        target.FanChannelOrder = source.FanChannelOrder;
        target.DashboardLayout = source.DashboardLayout;
        target.OverlayWidgetsEnabled = source.OverlayWidgetsEnabled;
        target.OverlayWidgetsAlwaysOnTop = source.OverlayWidgetsAlwaysOnTop;
        target.OverlayWidgetScale = source.OverlayWidgetScale;
        target.OverlayWidgetOpacity = source.OverlayWidgetOpacity;
        target.OverlayWidgetsMonitor = source.OverlayWidgetsMonitor;
        target.OverlayLayout = source.OverlayLayout ?? new();
    }
}
