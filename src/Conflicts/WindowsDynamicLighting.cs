using System;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Nexus.Service.Models.Conflicts;

namespace Nexus.Service.Conflicts;

/// <summary>
/// Reads and writes the Windows Dynamic Lighting settings that decide whether
/// Windows drives the same HID LampArray devices as Nexus.
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
    private const string DevicesPath = LightingPath + @"\Devices";
    private const string AmbientEnabledValue = "AmbientLightingEnabled";
    private const string ForegroundControlValue = "ControlledByForegroundApp";

    /// <summary>Whether this platform can carry the settings at all - the SPA hides the section otherwise.</summary>
    public static bool IsSupported() => OperatingSystem.IsWindows();

    /// <summary>
    /// A missing value reads as ON: Windows writes these only once the user has
    /// touched the page, and both default to on, so treating absent as off
    /// would show the section already handled on a machine where Windows is
    /// still driving the lights.
    /// </summary>
    public static bool DwordIsOn(object? value) => value is not int number || number != 0;

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
            state.ForegroundAppControl = DwordIsOn(root.GetValue(ForegroundControlValue));

            using var devices = Registry.Users.OpenSubKey($@"{sid}\{DevicesPath}");
            if (devices is null) return state;
            foreach (var name in devices.GetSubKeyNames())
            {
                using var device = devices.OpenSubKey(name);
                if (device is null) continue;
                state.DeviceCount++;
                if (DwordIsOn(device.GetValue(AmbientEnabledValue))) state.DevicesEnabled++;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[conflicts] dynamic lighting read failed: {ex.Message}");
        }
        return state;
    }

    /// <summary>Applies the settings the caller named, a null leaving that one alone, and returns the state re-read afterwards.</summary>
    [SupportedOSPlatform("windows")]
    public static WindowsDynamicLightingState Write(bool? enabled, bool? foregroundAppControl, bool? deviceLighting)
    {
        var sid = Nexus.Service.Lifecycle.ConsoleUserSid.Resolve(LightingPath);
        if (sid is null) return new WindowsDynamicLightingState();

        try
        {
            if (enabled is not null || foregroundAppControl is not null)
            {
                using var root = Registry.Users.OpenSubKey($@"{sid}\{LightingPath}", writable: true);
                // A null open here is the access-denied case, which otherwise
                // reaches the user as a toggle that simply does not move.
                if (root is null) Console.Error.WriteLine($"[conflicts] dynamic lighting: cannot open {LightingPath} for write");
                if (enabled is not null) root?.SetValue(AmbientEnabledValue, enabled.Value ? 1 : 0, RegistryValueKind.DWord);
                if (foregroundAppControl is not null) root?.SetValue(ForegroundControlValue, foregroundAppControl.Value ? 1 : 0, RegistryValueKind.DWord);
            }

            if (deviceLighting is not null)
            {
                using var devices = Registry.Users.OpenSubKey($@"{sid}\{DevicesPath}", writable: true);
                if (devices is null) Console.Error.WriteLine($"[conflicts] dynamic lighting: cannot open {DevicesPath} for write");
                foreach (var name in devices?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var device = devices!.OpenSubKey(name, writable: true);
                    if (device is null) Console.Error.WriteLine($"[conflicts] dynamic lighting: cannot open device {name} for write");
                    device?.SetValue(AmbientEnabledValue, deviceLighting.Value ? 1 : 0, RegistryValueKind.DWord);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[conflicts] dynamic lighting write failed: {ex.Message}");
        }

        return Read();
    }
#else
    public static WindowsDynamicLightingState Read() => new();

    public static WindowsDynamicLightingState Write(bool? enabled, bool? foregroundAppControl, bool? deviceLighting) => new();
#endif
}
