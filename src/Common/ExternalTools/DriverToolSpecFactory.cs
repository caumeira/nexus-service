using System;
using System.Globalization;
using System.IO;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Models.Widgets;

namespace Nexus.Service.Common.ExternalTools;

/// <summary>
/// Glue between an app's <c>driver</c> manifest block and an
/// <see cref="ExternalToolSpec"/>. Maps a live USB match to a variant and builds
/// the per-variant store URLs. Used by both the auto-launch device worker and the
/// dispatch actions so they resolve the same spec.
/// </summary>
public static class DriverToolSpecFactory
{
    /// <summary>True when a device matching the driver's VID + one of its PIDs is on the bus.</summary>
    public static bool DevicePresent(AppManifestDriver driver, IUsbEnumerator usb)
        => FindPresentVariant(driver, usb) is not null;

    /// <summary>
    /// Build a spec for whichever matching variant is currently on the bus, or null
    /// when no matching device is present.
    /// </summary>
    public static ExternalToolSpec? ResolvePresent(AppManifestDriver driver, string appBundleDir, IUsbEnumerator usb)
    {
        var variant = FindPresentVariant(driver, usb);
        return variant is null ? null : Build(driver, variant, appBundleDir);
    }

    /// <summary>Build a spec for an explicit variant (used by the worker once it knows the PID).</summary>
    public static ExternalToolSpec Build(AppManifestDriver driver, string variant, string appBundleDir)
    {
        var session = string.Equals(driver.Launch?.Session, "user", StringComparison.OrdinalIgnoreCase)
            ? ToolSession.User
            : ToolSession.System;
        var hidden = driver.Launch?.Hidden ?? true;
        var manifestBase = driver.ManifestUrlBase.TrimEnd('/');

        return new ExternalToolSpec(
            ToolId: driver.ToolId,
            Variant: variant,
            ManifestUrl: $"{manifestBase}/{variant}/latest.json",
            DownloadUrlBase: $"{manifestBase}/{variant}",
            FilePattern: driver.FilePattern,
            Launch: new ToolLaunchOptions(hidden, session),
            // An OEM image may ship the binary preloaded under the app bundle at
            // drivers/<variant>/ (with a bundled.json pin) so first boot is offline.
            PreloadDir: Path.Combine(appBundleDir, "drivers", variant));
    }

    /// <summary>Return the variant name for the first matching device on the bus, or null.</summary>
    private static string? FindPresentVariant(AppManifestDriver driver, IUsbEnumerator usb)
    {
        if (driver.Match is null || !TryParseHex(driver.Match.Vid, out var vid))
            return null;

        foreach (var device in usb.Enumerate())
        {
            if (device.VendorId != vid) continue;
            foreach (var pidHex in driver.Match.Pids)
            {
                if (TryParseHex(pidHex, out var pid) && device.ProductId == pid)
                    return driver.Variants.TryGetValue(pidHex, out var name) ? name : pidHex;
            }
        }
        return null;
    }

    private static bool TryParseHex(string? value, out int result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
    }
}
