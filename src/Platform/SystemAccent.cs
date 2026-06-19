using System;
using System.Collections.Generic;
using System.Globalization;

namespace Nexus.Service.Platform;

/// <summary>
/// Reads the desktop's OS accent colour. Windows and macOS push the accent into
/// the web straight from their native app shell (DWM / NSColor), so those return
/// null here. Linux has no native shell - the dashboard is a plain browser - so
/// the service reads the accent from the XDG desktop portal and serves it over
/// <c>GET /system/accent</c> for the web to apply when the accent source is
/// "system".
/// </summary>
public interface ISystemAccentProvider
{
    /// <summary>OS accent as #RRGGBB, or null when unavailable / handled by the native shell.</summary>
    string? GetAccentHex();
}

/// <summary>Windows/macOS (shell pushes the accent) and headless hosts.</summary>
public sealed class NullSystemAccentProvider : ISystemAccentProvider
{
    public string? GetAccentHex() => null;
}

/// <summary>
/// Parses the XDG portal accent-color reply. Lives here (not in the Linux-only
/// provider) so it stays cross-platform-compiled and unit-testable on the host.
/// </summary>
public static class PortalAccent
{
    /// <summary>
    /// gdbus prints the <c>(ddd)</c> RGB tuple (0..1) as e.g.
    /// <c>(&lt;(0.34, 0.62, 0.80)&gt;,)</c>. Returns #RRGGBB, or null when the
    /// tuple is absent or signals "no accent" (a negative component, per spec).
    /// </summary>
    public static string? Parse(string? gdbusOutput)
    {
        if (string.IsNullOrWhiteSpace(gdbusOutput))
            return null;
        var nums = new List<double>(3);
        foreach (var token in gdbusOutput.Split(
            new[] { '(', ')', '<', '>', ',', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                nums.Add(v);
                if (nums.Count == 3)
                    break;
            }
        }
        if (nums.Count < 3 || nums[0] < 0 || nums[1] < 0 || nums[2] < 0)
            return null;
        return $"#{Channel(nums[0])}{Channel(nums[1])}{Channel(nums[2])}";
    }

    private static string Channel(double v) =>
        ((int)Math.Round(Math.Clamp(v, 0d, 1d) * 255)).ToString("X2", CultureInfo.InvariantCulture);
}
