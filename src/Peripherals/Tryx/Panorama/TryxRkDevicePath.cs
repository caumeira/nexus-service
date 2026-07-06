using System;
using System.Globalization;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>
/// Parses fields out of the Windows usbprint device-interface path, which looks
/// like \\?\USB#VID_391A&amp;PID_1011#&lt;serial&gt;#{28d78fad-...}. Kept free of the
/// WINDOWS guard on <see cref="WindowsTryxRkDiscovery"/> so it builds and unit-tests
/// on every platform.
/// </summary>
internal static class TryxRkDevicePath
{
    public static string ParseSerial(string devicePath)
    {
        var parts = devicePath.Split('#');
        return parts.Length > 2 ? parts[2] : "";
    }

    /// <summary>Reads the four-hex-digit PID from the path's PID_ token, or returns
    /// <paramref name="fallback"/> when the path carries no parseable PID.</summary>
    public static int ParseProductId(string devicePath, int fallback)
    {
        const string marker = "PID_";
        var start = devicePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return fallback;
        start += marker.Length;
        if (start + 4 > devicePath.Length) return fallback;
        return int.TryParse(devicePath.AsSpan(start, 4), NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out var pid) ? pid : fallback;
    }
}
