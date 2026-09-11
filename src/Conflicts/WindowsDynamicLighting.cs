using System;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Nexus.Service.Models.Conflicts;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Conflicts;

/// <summary>
/// Reads and writes the one Windows Dynamic Lighting setting that decides
/// whether Windows drives the same HID LampArray devices as Nexus. Its other
/// settings - foreground-app handover, per-device switches, brightness and
/// effects - only decide anything while this one is on, which is the state the
/// conflict exists in.
///
/// The value names are the ones
/// <c>C:\Windows\System32\SettingsHandlers_Lighting.dll</c> carries, which is
/// what the Settings > Personalization > Dynamic Lighting page writes. They
/// live in the interactive user's hive, so a LocalSystem service has to go
/// through <see cref="Nexus.Service.Lifecycle.ConsoleUserSid"/> rather than
/// HKCU, which would resolve to the service account.
/// </summary>
public static class WindowsDynamicLighting
{
    private const string LightingPath = @"Software\Microsoft\Lighting";
    private const string AmbientEnabledValue = "AmbientLightingEnabled";

    /// <summary>HID Lighting And Illumination page, LampArray usage - what Windows itself selects on.</summary>
    private const int LampArrayUsagePage = 0x59;
    private const int LampArrayUsage = 0x01;

    /// <summary>Whether this platform can carry the settings at all - the SPA hides the section otherwise.</summary>
    public static bool IsSupported() => OperatingSystem.IsWindows();

    /// <summary>
    /// A missing value reads as ON: Windows writes it only once the user has
    /// touched the page, and it defaults to on, so treating absent as off
    /// would show the section already handled on a machine where Windows is
    /// still driving the lights.
    /// </summary>
    public static bool DwordIsOn(object? value) => value is not int number || number != 0;

    /// <summary>
    /// LampArray interfaces out of a HID enumeration, named the way Windows
    /// names their <c>Devices</c> entry: the interface path without its
    /// <c>\\?\</c> prefix.
    /// </summary>
    public static List<string> LampArrayIds(IEnumerable<HidDeviceInfo> devices)
    {
        var ids = new List<string>();
        foreach (var hid in devices)
        {
            if (hid.UsagePage != LampArrayUsagePage || hid.Usage != LampArrayUsage) continue;
            ids.Add(hid.Path.StartsWith(@"\\?\", StringComparison.Ordinal) ? hid.Path.Substring(4) : hid.Path);
        }
        return ids;
    }

#if WINDOWS
    /// <summary>
    /// Current state, or <see cref="WindowsDynamicLightingState.Available"/>
    /// false when the console user's Lighting key cannot be read (no console
    /// user, unloaded profile, or a Windows build without the feature).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static WindowsDynamicLightingState Read()
    {
        var state = new WindowsDynamicLightingState();
        var sid = Nexus.Service.Lifecycle.ConsoleUserSid.Resolve(LightingPath);
        if (sid is null) return state;

        try
        {
            using var root = Registry.Users.OpenSubKey($@"{sid}\{LightingPath}");
            if (root is null) return state;

            state.Available = true;
            state.Enabled = DwordIsOn(root.GetValue(AmbientEnabledValue));
            // Windows keeps a Devices entry for every LampArray it has ever
            // seen, so counting those subkeys reports hardware that is not
            // plugged in. Count what it can drive right now instead - with
            // nothing attached there is no conflict to show.
            state.DeviceCount = PresentLampArrays().Count;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[conflicts] dynamic lighting read failed: {ex.Message}");
        }
        return state;
    }

    /// <summary>Turns Windows' own lighting on or off, and returns the state re-read afterwards.</summary>
    [SupportedOSPlatform("windows")]
    public static WindowsDynamicLightingState Write(bool enabled)
    {
        var sid = Nexus.Service.Lifecycle.ConsoleUserSid.Resolve(LightingPath);
        if (sid is null) return new WindowsDynamicLightingState();

        try
        {
            using var root = Registry.Users.OpenSubKey($@"{sid}\{LightingPath}", writable: true);
            // A null open here is the access-denied case, which otherwise
            // reaches the user as a toggle that simply does not move.
            if (root is null) Console.Error.WriteLine($"[conflicts] dynamic lighting: cannot open {LightingPath} for write");
            root?.SetValue(AmbientEnabledValue, enabled ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[conflicts] dynamic lighting write failed: {ex.Message}");
        }

        return Read();
    }

    /// <summary>
    /// Interface ids of the LampArray devices connected right now, on the same
    /// HID usage the Windows settings page selects on. Enumeration walks every
    /// HID interface and opens each one query-only.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static List<string> PresentLampArrays()
    {
        try
        {
            return LampArrayIds(new Nexus.Service.Peripherals.Hid.WindowsHidEnumerator().FindAll());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[conflicts] dynamic lighting device scan failed: {ex.Message}");
            return new List<string>();
        }
    }
#else
    public static WindowsDynamicLightingState Read() => new();

    public static WindowsDynamicLightingState Write(bool enabled) => new();
#endif
}
