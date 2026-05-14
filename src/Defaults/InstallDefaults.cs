using System;
using System.Collections.Generic;
using System.Text.Json;
using Qos.Service.Serialization;

namespace Qos.Service.Defaults;

/// <summary>
/// Canonical install-time defaults. Data lives in data/install-defaults.json
/// (shipped as an embedded resource). Loads once on first access and exposes
/// typed accessors so every subsystem can read the same canonical values
/// instead of hardcoding literal initializers.
///
/// The qos-web debug-tools export menu serializes the running config in this
/// exact shape so the user can paste the output back to the AI; the AI then
/// rewrites install-defaults.json and ships. One file, one source of truth.
///
/// To add a default: extend the matching POCO below + add the field to the
/// JSON file + reference it from the consuming subsystem.
/// </summary>
public static class InstallDefaults
{
    private static readonly Lazy<InstallDefaultsDocument> _doc = new(Load);

    /// <summary>
    /// Singleton instance loaded from the embedded JSON. Treat as immutable —
    /// mutating any nested property poisons every subsequent reader. The
    /// /defaults and /defaults/snapshot routes serialize this directly; if a
    /// caller ever needs to mutate, project to a new <see cref="InstallDefaultsDocument"/> first.
    /// </summary>
    public static InstallDefaultsDocument All => _doc.Value;
    public static ThemeDefaults Theme => All.Theme;
    public static MonitoringDefaults Monitoring => All.Monitoring;
    public static PanelDefaults Panel => All.Panel;
    public static OverlayDefaults Overlay => All.Overlay;
    public static LightingDefaults Lighting => All.Lighting;
    public static Y70Defaults Y70 => All.Y70;
    public static KeebDefaults Keeb => All.Keeb;
    public static CoolingDefaults Cooling => All.Cooling;
    public static ObsDefaults Obs => All.Obs;
    public static ScreenTimeDefaults ScreenTime => All.ScreenTime;
    public static CnvsDefaults Cnvs => All.Cnvs;
    public static AuthDefaults Auth => All.Auth;

    private static InstallDefaultsDocument Load()
    {
        var asm = typeof(InstallDefaults).Assembly;
        using var stream = asm.GetManifestResourceStream("install-defaults.json");
        if (stream is null)
        {
            Console.Error.WriteLine("[defaults] install-defaults.json resource not found; using empty defaults");
            return new InstallDefaultsDocument();
        }
        try
        {
            return JsonSerializer.Deserialize(stream, AppJsonContext.Default.InstallDefaultsDocument)
                ?? new InstallDefaultsDocument();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[defaults] failed to parse install-defaults.json: {ex.Message}");
            return new InstallDefaultsDocument();
        }
    }
}

// ── POCOs mirroring install-defaults.json ─────────────────────────────────
// camelCase JSON ↔ PascalCase C# via AppJsonContext's CamelCase naming policy.
// Default property values here only matter when the JSON load fails entirely;
// the file is the real source of truth.

public sealed class InstallDefaultsDocument
{
    public ThemeDefaults Theme { get; set; } = new();
    public MonitoringDefaults Monitoring { get; set; } = new();
    public PanelDefaults Panel { get; set; } = new();
    public OverlayDefaults Overlay { get; set; } = new();
    public LightingDefaults Lighting { get; set; } = new();
    public Y70Defaults Y70 { get; set; } = new();
    public KeebDefaults Keeb { get; set; } = new();
    public CoolingDefaults Cooling { get; set; } = new();
    public ObsDefaults Obs { get; set; } = new();
    public ScreenTimeDefaults ScreenTime { get; set; } = new();
    public CnvsDefaults Cnvs { get; set; } = new();
    public AuthDefaults Auth { get; set; } = new();
}

public sealed class ThemeDefaults
{
    public string Language { get; set; } = "en";
    public string ThemeMode { get; set; } = "system";
    public string AccentColor { get; set; } = "#8b5cf6";
}

public sealed class MonitoringDefaults
{
    public bool ShowAverage { get; set; } = true;
    public bool ShowMacStatusBarIcon { get; set; } = true;
    public bool ShowWindowsTrayIcon { get; set; } = true;
}

public sealed class PanelDefaults
{
    public bool AutoLaunch { get; set; }
    public bool ThemeSyncWithDesktop { get; set; } = true;
    public string ThemeMode { get; set; } = "system";
    public bool AccentSyncWithDesktop { get; set; } = true;
    public string BackgroundMode { get; set; } = "solid";
    public string BackgroundEffect { get; set; } = "aurora";
    public int BackgroundTemplate { get; set; }
    public double BackgroundOpacity { get; set; } = 0.4;
    public double WidgetOpacity { get; set; } = 1.0;
    public bool WidgetLabels { get; set; } = true;
    public PanelLayoutsDefaults Layouts { get; set; } = new();
}

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
}

public sealed class OverlayDefaults
{
    public bool Enabled { get; set; }
    public bool AlwaysOnTop { get; set; }
    public int Scale { get; set; } = 100;
    public double Opacity { get; set; } = 1.0;
    public int Monitor { get; set; } = -1;
}

public sealed class LightingDefaults
{
    public string Sync { get; set; } = "none";
    public bool BrightnessEnabled { get; set; }
    public bool SpeedEnabled { get; set; }
    public int FrameRate { get; set; } = 60;
    public double ScaleRatio { get; set; } = 1.0;
    public bool MusicReactive { get; set; }
    public LightingStaticColor StaticColor { get; set; } = new();
    public LightingAnimateDefaults Animate { get; set; } = new();
    public LightingPostProcess PostProcess { get; set; } = new();
    public LightingDevicePreferenceDefaults DevicePreference { get; set; } = new();
}

public sealed class LightingStaticColor
{
    public byte R { get; set; } = 255;
    public byte G { get; set; }
    public byte B { get; set; }
}

public sealed class LightingAnimateDefaults
{
    public string Effect { get; set; } = "rainbow";
    public LightingAnimateState State { get; set; } = new();
}

public sealed class LightingAnimateState
{
    public int Speed { get; set; } = 50;
    public float Intensity { get; set; } = 1f;
    public float Hue { get; set; }
    public float Colorize { get; set; }
    public float Saturation { get; set; } = 1f;
    public float Contrast { get; set; } = 1f;
}

public sealed class LightingPostProcess
{
    public float Hue { get; set; }
    public float Colorize { get; set; }
    public float Saturation { get; set; } = 1f;
    public float Contrast { get; set; } = 1f;
}

public sealed class LightingDevicePreferenceDefaults
{
    public int Brightness { get; set; } = 100;
    public float Saturation { get; set; } = 1.0f;
}

public sealed class Y70Defaults
{
    public string Orientation { get; set; } = "Landscape";
    public int Brightness { get; set; } = 80;
    public bool ScreenOff { get; set; }
}

public sealed class KeebDefaults
{
    public string RotaryLeft { get; set; } = "VolumeAdjustment";
    public string RotaryRight { get; set; } = "BrightnessAdjustment";
    public string RotarySensitivity { get; set; } = "Balanced";
    public KeebFirmwareLightingDefaults FirmwareLighting { get; set; } = new();
}

public sealed class KeebFirmwareLightingDefaults
{
    public string AnimationMode { get; set; } = "Static";
    public string Speed { get; set; } = "Medium";
    public string Direction { get; set; } = "Forward";
    public int Brightness { get; set; } = 80;
    public bool KeyReactive { get; set; }
    public bool KeyReactiveMask { get; set; }
    public string KeyReactiveMode { get; set; } = "Off";
}

public sealed class CoolingDefaults
{
    public double GlobalSpeedModifier { get; set; } = 1.0;
    public string ActivePreset { get; set; } = "custom";
    public Dictionary<string, CoolingPresetDefault> Presets { get; set; } = new();
    public CoolingDeviceLayoutSize DeviceLayoutSize { get; set; } = new();
}

public sealed class CoolingPresetDefault
{
    public double ResponseTime { get; set; }
    public double MinTemp { get; set; }
    public double MaxTemp { get; set; }
    public double MinSpeed { get; set; }
    public double MaxSpeed { get; set; }
}

public sealed class CoolingDeviceLayoutSize
{
    public float W { get; set; } = 80;
    public float H { get; set; } = 80;
}

public sealed class ObsDefaults
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4455;
}

public sealed class ScreenTimeDefaults
{
    public bool TrackingEnabled { get; set; } = true;
}

public sealed class CnvsDefaults
{
    public bool PlayAnimation { get; set; } = true;
    public bool PlayWhenPCOff { get; set; }
}

public sealed class AuthDefaults
{
    public bool RemoteControlEnabled { get; set; } = true;
}
