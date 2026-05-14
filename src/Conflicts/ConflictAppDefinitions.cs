using System;
using System.Collections.Generic;

namespace Qos.Service.Conflicts;

/// <summary>
/// Static catalog of third-party apps that compete with Qos for hardware
/// control (RGB lighting, fan speeds, peripheral firmware, GPU overlays).
/// The ConflictWatcher scans running processes and surfaces any match in
/// the sidebar warning.
///
/// New entries can be added freely — the watcher dedupes by Id. ProcessNames
/// use the OS-level <see cref="System.Diagnostics.Process.ProcessName"/>
/// convention: no .exe suffix on Windows, just the executable basename.
/// Comparisons are case-insensitive.
///
/// Layout mirrors the HYTE Nexus RgbAppRegistry (the user's reference list)
/// with Qos categories grafted on for the sidebar UI.
/// </summary>
public sealed class ConflictAppDefinition
{
    /// <summary>Stable identifier surfaced over the wire (kebab-case).</summary>
    public string Id { get; init; } = "";

    /// <summary>Human-readable name shown in the warning UI.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>One of "lighting", "cooling", "peripherals", "monitoring" — drives the UI hint.</summary>
    public string Category { get; init; } = "";

    /// <summary>Process names to match against <c>Process.GetProcesses().ProcessName</c>.</summary>
    public string[] ProcessNames { get; init; } = Array.Empty<string>();
}

public static class ConflictAppCatalog
{
    /// <summary>
    /// Curated list of apps that visibly fight Qos when run alongside it.
    /// Mirrors HYTE Nexus's RgbAppRegistry so existing Nexus users see the
    /// same coverage in Qos. ProcessNames are the Windows
    /// <see cref="System.Diagnostics.Process.ProcessName"/> form (basename
    /// without the .exe extension).
    /// </summary>
    public static readonly IReadOnlyList<ConflictAppDefinition> All = new ConflictAppDefinition[]
    {
        // ── NZXT ────────────────────────────────────────────────────────────
        new()
        {
            Id = "nzxt-cam",
            DisplayName = "NZXT CAM",
            Category = "lighting",
            ProcessNames = new[] { "NZXT CAM", "CAM", "NZXT CAM Beta", "NZXT CAM Service", "NZXTCAM" },
        },
        new()
        {
            Id = "nzxt-kraken",
            DisplayName = "NZXT Kraken",
            Category = "cooling",
            ProcessNames = new[] { "NZXT Kraken" },
        },

        // ── SignalRGB / OpenRGB ────────────────────────────────────────────
        new()
        {
            Id = "signalrgb",
            DisplayName = "SignalRGB",
            Category = "lighting",
            ProcessNames = new[] { "SignalRgb", "SignalRgbLauncher", "SignalRgbService", "SignalRgb.Service" },
        },
        new()
        {
            Id = "openrgb",
            DisplayName = "OpenRGB",
            Category = "lighting",
            // Qos bundles its own headless OpenRGB subprocess (see qos-rgb). A
            // user-launched OpenRGB.exe with its own GUI will conflict, but our
            // bundled child process runs from inside our install dir, so the
            // watcher filters it out at scan time (see ConflictWatcher).
            ProcessNames = new[] { "OpenRGB" },
        },

        // ── ASUS ───────────────────────────────────────────────────────────
        new()
        {
            Id = "asus-ai-suite-3",
            DisplayName = "ASUS AI Suite 3",
            Category = "cooling",
            // AISuite3 = real binary. AlSuite3 is a propagated typo from the
            // upstream Nexus registry kept as a defensive alias.
            ProcessNames = new[] { "AISuite3", "AlSuite3" },
        },
        new()
        {
            Id = "armoury-crate",
            DisplayName = "ASUS Armoury Crate",
            Category = "lighting",
            ProcessNames = new[] { "ArmouryCrate", "ArmouryCrate.Service", "ArmouryCrate.UserSessionHelper", "ArmourySocketServer", "Armoury Crate" },
        },
        new()
        {
            Id = "asus-lighting-service",
            DisplayName = "ASUS Lighting Service",
            Category = "lighting",
            ProcessNames = new[] { "LightingService" },
        },
        new()
        {
            Id = "aura-sync",
            DisplayName = "ASUS Aura Sync",
            Category = "lighting",
            ProcessNames = new[] { "AuraSync", "AsusAura" },
        },
        new()
        {
            Id = "asus-fan-xpert",
            DisplayName = "ASUS Fan Xpert",
            Category = "cooling",
            ProcessNames = new[] { "FanXpert" },
        },

        // ── ASRock ─────────────────────────────────────────────────────────
        new()
        {
            Id = "asrock-rgb-sync",
            DisplayName = "ASRock RGB Sync",
            Category = "lighting",
            ProcessNames = new[] { "ASRRGBLED" },
        },
        new()
        {
            Id = "asrock-polychrome",
            DisplayName = "ASRock Polychrome RGB",
            Category = "lighting",
            ProcessNames = new[] { "AsrPolychromeRGB" },
        },

        // ── MSI ────────────────────────────────────────────────────────────
        new()
        {
            Id = "msi-afterburner",
            DisplayName = "MSI Afterburner",
            Category = "monitoring",
            ProcessNames = new[] { "MSIAfterburner" },
        },
        new()
        {
            Id = "msi-mystic-light",
            DisplayName = "MSI Mystic Light",
            Category = "lighting",
            ProcessNames = new[] { "Mystic_Light", "MysticLight", "MysticLight_x64" },
        },
        new()
        {
            Id = "msi-center",
            DisplayName = "MSI Center",
            Category = "lighting",
            ProcessNames = new[] { "MSI.CentralServer", "MSI Center" },
        },
        new()
        {
            Id = "msi-gaming-center",
            DisplayName = "MSI Gaming Center",
            Category = "lighting",
            ProcessNames = new[] { "GCC" },
        },
        new()
        {
            Id = "msi-control-center",
            DisplayName = "MSI Control Center",
            Category = "monitoring",
            ProcessNames = new[] { "ControlCenter" },
        },
        new()
        {
            Id = "msi-led-keeper",
            DisplayName = "MSI LED Keeper",
            Category = "lighting",
            ProcessNames = new[] { "LedKeeper", "LEDKeeper2" },
        },
        new()
        {
            Id = "msi-companion",
            DisplayName = "MSI Companion",
            Category = "monitoring",
            ProcessNames = new[] { "MSI_Companion_Service" },
        },
        new()
        {
            Id = "msi-game-bar-tool",
            DisplayName = "MSI Game Bar Tool",
            Category = "monitoring",
            ProcessNames = new[] { "MSI_GamebarTool" },
        },
        new()
        {
            Id = "msi-super-charger",
            DisplayName = "MSI Super Charger",
            Category = "monitoring",
            ProcessNames = new[] { "MSI_Super_Charger_Service" },
        },

        // ── Gigabyte ───────────────────────────────────────────────────────
        new()
        {
            Id = "gigabyte-rgb-fusion",
            DisplayName = "Gigabyte RGB Fusion",
            Category = "lighting",
            ProcessNames = new[] { "RGBFusion", "RGBFusion2.0", "RGB Fusion" },
        },
        new()
        {
            Id = "gigabyte-aorus",
            DisplayName = "Gigabyte AORUS Engine",
            Category = "monitoring",
            ProcessNames = new[] { "AORUS" },
        },
        new()
        {
            Id = "gigabyte-smart-fan",
            DisplayName = "Gigabyte Smart Fan",
            Category = "cooling",
            ProcessNames = new[] { "SmartFan" },
        },

        // ── Corsair ────────────────────────────────────────────────────────
        new()
        {
            Id = "icue",
            DisplayName = "Corsair iCUE",
            Category = "lighting",
            ProcessNames = new[]
            {
                "iCUE", "iCUE Launcher", "iCUELauncher", "iCUEDevicePluginHost",
                "Corsair.Service", "Corsair.Service.CpuldRemote64", "Corsair.Service.DisplayAdapter",
                "CorsairDeviceControlService", "CueLLAccessService", "CorsairService",
            },
        },
        new()
        {
            Id = "corsair-link4",
            DisplayName = "Corsair Link 4",
            Category = "cooling",
            ProcessNames = new[] { "CorsairLink4" },
        },

        // ── Razer ──────────────────────────────────────────────────────────
        new()
        {
            Id = "razer-synapse",
            DisplayName = "Razer Synapse",
            Category = "peripherals",
            ProcessNames = new[] { "Razer Synapse 3", "RzSynapse", "Razer Synapse Service", "RazerCentralService", "Razer Central" },
        },
        new()
        {
            Id = "razer-chroma-sdk",
            DisplayName = "Razer Chroma SDK",
            Category = "lighting",
            ProcessNames = new[] { "Razer Chroma SDK Service" },
        },

        // ── Logitech ───────────────────────────────────────────────────────
        new()
        {
            Id = "logitech-ghub",
            DisplayName = "Logitech G HUB",
            Category = "peripherals",
            ProcessNames = new[] { "lghub", "lghub_agent", "lghub_system_tray", "logi_overlay" },
        },

        // ── Lian Li ────────────────────────────────────────────────────────
        new()
        {
            Id = "lian-li-l-connect",
            DisplayName = "Lian Li L-Connect",
            Category = "lighting",
            ProcessNames = new[] { "L-Connect 3", "L-Connect", "LConnect3", "LConnect" },
        },

        // ── Other peripheral / lighting vendors ────────────────────────────
        new()
        {
            Id = "glorious-core",
            DisplayName = "Glorious Core",
            Category = "peripherals",
            ProcessNames = new[] { "Glorious Core" },
        },
        new()
        {
            Id = "fnatic-op",
            DisplayName = "Fnatic OP",
            Category = "peripherals",
            ProcessNames = new[] { "Fnatic OP" },
        },
        new()
        {
            Id = "steelseries-engine",
            DisplayName = "SteelSeries Engine",
            Category = "peripherals",
            ProcessNames = new[] { "SteelSeriesEngine" },
        },
        new()
        {
            Id = "steelseries-gg",
            DisplayName = "SteelSeries GG",
            Category = "peripherals",
            ProcessNames = new[] { "SteelSeriesGG", "SteelSeriesGGClient" },
        },
        new()
        {
            Id = "steelseries-prism",
            DisplayName = "SteelSeries Prism",
            Category = "lighting",
            ProcessNames = new[] { "SteelSeriesPrism" },
        },
        new()
        {
            Id = "roccat-swarm",
            DisplayName = "ROCCAT Swarm",
            Category = "peripherals",
            ProcessNames = new[] { "ROCCAT_Swarm", "ROCCAT_Swarm_Monitor", "ROCCAT_dev_service" },
        },
        new()
        {
            Id = "xpg-prime",
            DisplayName = "XPG Prime",
            Category = "lighting",
            ProcessNames = new[] { "XPG-Prime" },
        },

        // ── EVGA ───────────────────────────────────────────────────────────
        new()
        {
            Id = "evga-precision-x1",
            DisplayName = "EVGA Precision X1",
            Category = "monitoring",
            ProcessNames = new[] { "PrecisionX_x64" },
        },
        new()
        {
            Id = "evga-precision-x-server",
            DisplayName = "EVGA Precision X Server",
            Category = "monitoring",
            ProcessNames = new[] { "EVGAPrecisionXServer" },
        },
        new()
        {
            Id = "evga-aio",
            DisplayName = "EVGA AIO Control",
            Category = "cooling",
            ProcessNames = new[] { "EVGAAIO" },
        },

        // ── Thermaltake ────────────────────────────────────────────────────
        new()
        {
            Id = "thermaltake-itake",
            DisplayName = "Thermaltake iTAKE Engine",
            Category = "lighting",
            ProcessNames = new[] { "TT iTAKE Engine" },
        },
        new()
        {
            Id = "thermaltake-rgb-plus",
            DisplayName = "Thermaltake RGB Plus",
            Category = "lighting",
            // TTRGBPlusGUI = real binary (the upstream Nexus registry has a
            // lowercase-L typo "TTRGBPlusGUl" we keep as a defensive alias).
            ProcessNames = new[] { "TTRGBPlus", "TTRGBPlusGUI", "TTRGBPlusGUl" },
        },
        new()
        {
            Id = "thermaltake-dps-g",
            DisplayName = "Thermaltake DPS G",
            Category = "monitoring",
            ProcessNames = new[] { "TT DPS G" },
        },

        // ── Cooler Master ──────────────────────────────────────────────────
        new()
        {
            Id = "cooler-master-plus",
            DisplayName = "Cooler Master MasterPlus+",
            Category = "lighting",
            ProcessNames = new[] { "CoolerMasterPlus" },
        },

        // ── Fan / temp control ─────────────────────────────────────────────
        new()
        {
            Id = "fan-control",
            DisplayName = "FanControl",
            Category = "cooling",
            ProcessNames = new[] { "FanControl" },
        },
        new()
        {
            Id = "speedfan",
            DisplayName = "SpeedFan",
            Category = "cooling",
            ProcessNames = new[] { "speedfan" },
        },
        new()
        {
            Id = "argus-monitor",
            DisplayName = "Argus Monitor",
            Category = "monitoring",
            ProcessNames = new[] { "ArgusMonitor" },
        },
        new()
        {
            Id = "notebook-fan-control",
            DisplayName = "NoteBook Fan Control",
            Category = "cooling",
            ProcessNames = new[] { "NoteBookFanControl" },
        },
        new()
        {
            Id = "fanctrl",
            DisplayName = "FanCtrl",
            Category = "cooling",
            ProcessNames = new[] { "FanCtrl" },
        },
        new()
        {
            Id = "argb-fan-master",
            DisplayName = "Argb Fan Master",
            Category = "lighting",
            ProcessNames = new[] { "ArgbFanMaster" },
        },
    };
}
