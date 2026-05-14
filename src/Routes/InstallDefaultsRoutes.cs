using Qos.Service.Auth;
using Qos.Service.Defaults;
using Qos.Service.Persistence;

namespace Qos.Service.Routes;

public static class InstallDefaultsRoutes
{
    /// <summary>
    /// Read-only access to the canonical install-defaults table baked
    /// into the AOT binary at data/install-defaults.json. The companion
    /// /defaults/snapshot reads the *live* running config and projects
    /// it back into the same shape; the SPA's debug-tools menu copies
    /// that snapshot to the clipboard so the user can paste it to the
    /// AI to overwrite install-defaults.json.
    /// </summary>
    public static void MapDefaultsEndpoints(this WebApplication app)
    {
        app.MapGet("/defaults", () => InstallDefaults.All).AllowPanel();

        app.MapGet("/defaults/snapshot", (IConfigStore store) =>
        {
            var s = store.Load();
            return new InstallDefaultsDocument
            {
                Theme = new ThemeDefaults
                {
                    Language = s.Ui.Language,
                    ThemeMode = s.Ui.ThemeMode,
                    AccentColor = s.Ui.AccentColor,
                },
                Monitoring = new MonitoringDefaults
                {
                    ShowAverage = s.Ui.MonitoringShowAverage,
                    ShowMacStatusBarIcon = s.Ui.ShowMacStatusBarIcon,
                    ShowWindowsTrayIcon = s.Ui.ShowWindowsTrayIcon,
                },
                Panel = new PanelDefaults
                {
                    AutoLaunch = s.Ui.PanelAutoLaunch,
                    ThemeSyncWithDesktop = s.Ui.PanelThemeSyncWithDesktop,
                    ThemeMode = s.Ui.PanelThemeMode,
                    AccentSyncWithDesktop = s.Ui.PanelAccentSyncWithDesktop,
                    BackgroundMode = s.Ui.PanelBackgroundMode,
                    BackgroundEffect = s.Ui.PanelBackgroundEffect,
                    BackgroundTemplate = s.Ui.PanelBackgroundTemplate,
                    BackgroundOpacity = s.Ui.PanelBackgroundOpacity,
                    WidgetOpacity = s.Ui.PanelWidgetOpacity,
                    WidgetLabels = s.Ui.PanelWidgetLabels,
                    // Layouts stay sourced from the canonical install-defaults;
                    // live layouts are per-device records under PanelDevices and
                    // don't map back onto the four canonical surfaces directly.
                    Layouts = InstallDefaults.Panel.Layouts,
                },
                Overlay = new OverlayDefaults
                {
                    Enabled = s.Ui.OverlayWidgetsEnabled,
                    AlwaysOnTop = s.Ui.OverlayWidgetsAlwaysOnTop,
                    Scale = s.Ui.OverlayWidgetScale,
                    Opacity = s.Ui.OverlayWidgetOpacity,
                    Monitor = s.Ui.OverlayWidgetsMonitor,
                },
                Lighting = new LightingDefaults
                {
                    Sync = s.Lighting.Sync,
                    BrightnessEnabled = s.Lighting.BrightnessEnabled,
                    SpeedEnabled = s.Lighting.SpeedEnabled,
                    FrameRate = s.Lighting.FrameRate,
                    ScaleRatio = s.Lighting.ScaleRatio,
                    MusicReactive = s.Lighting.MusicReactive,
                    StaticColor = new LightingStaticColor
                    {
                        R = s.Lighting.StaticColor.R,
                        G = s.Lighting.StaticColor.G,
                        B = s.Lighting.StaticColor.B,
                    },
                    Animate = new LightingAnimateDefaults
                    {
                        Effect = s.Lighting.Animate.Effect,
                        // State is a per-effect dictionary at runtime; export
                        // the active effect's slider snapshot when present so
                        // pasting the JSON back installs that as the default.
                        State = s.Lighting.Animate.States.TryGetValue(s.Lighting.Animate.Effect, out var st) && st is not null
                            ? new LightingAnimateState
                            {
                                Speed = st.Speed,
                                Intensity = st.Intensity,
                                Hue = st.Hue,
                                Colorize = st.Colorize,
                                Saturation = st.Saturation,
                                Contrast = st.Contrast,
                            }
                            : InstallDefaults.Lighting.Animate.State,
                    },
                    PostProcess = new LightingPostProcess
                    {
                        Hue = s.Lighting.ScreenEffect.Hue,
                        Colorize = s.Lighting.ScreenEffect.Colorize,
                        Saturation = s.Lighting.ScreenEffect.Saturation,
                        Contrast = s.Lighting.ScreenEffect.Contrast,
                    },
                    DevicePreference = InstallDefaults.Lighting.DevicePreference,
                },
                Y70 = new Y70Defaults
                {
                    Orientation = s.Y70.Orientation,
                    Brightness = s.Y70.Brightness,
                    ScreenOff = s.Y70.ScreenOff,
                },
                Keeb = new KeebDefaults
                {
                    RotaryLeft = s.Keeb.RotaryLeft,
                    RotaryRight = s.Keeb.RotaryRight,
                    RotarySensitivity = s.Keeb.RotarySensitivity,
                    FirmwareLighting = new KeebFirmwareLightingDefaults
                    {
                        AnimationMode = s.Keeb.FirmwareLighting.AnimationMode,
                        Speed = s.Keeb.FirmwareLighting.Speed,
                        Direction = s.Keeb.FirmwareLighting.Direction,
                        Brightness = s.Keeb.FirmwareLighting.Brightness,
                        KeyReactive = s.Keeb.FirmwareLighting.KeyReactive,
                        KeyReactiveMask = s.Keeb.FirmwareLighting.KeyReactiveMask,
                        KeyReactiveMode = s.Keeb.FirmwareLighting.KeyReactiveMode,
                    },
                },
                Cooling = new CoolingDefaults
                {
                    GlobalSpeedModifier = s.Cooling.GlobalSpeedModifier,
                    ActivePreset = s.Cooling.ActivePreset,
                    Presets = InstallDefaults.Cooling.Presets,
                    DeviceLayoutSize = InstallDefaults.Cooling.DeviceLayoutSize,
                },
                Obs = new ObsDefaults
                {
                    Host = s.Obs.Host,
                    Port = s.Obs.Port,
                },
                ScreenTime = new ScreenTimeDefaults
                {
                    TrackingEnabled = s.ScreenTime.TrackingEnabled,
                },
                Cnvs = new CnvsDefaults
                {
                    PlayAnimation = s.Devices.Cnvs.PlayAnimation,
                    PlayWhenPCOff = s.Devices.Cnvs.PlayWhenPCOff,
                },
                Auth = new AuthDefaults
                {
                    RemoteControlEnabled = s.Auth?.RemoteControlEnabled ?? InstallDefaults.Auth.RemoteControlEnabled,
                },
            };
        }).AllowPanel();
    }
}
