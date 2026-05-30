using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Nexus.Service.Peripherals.Hyte;

/// <summary>
/// Shared Linux serial-port discovery for the HYTE USB-CDC hubs (CNVS, NP50,
/// MiniHub, Q-series cooler, Y70 display). Mirrors what the per-device
/// <c>Windows*PortDiscovery</c> classes do via SetupAPI, but reads the USB
/// identity straight out of sysfs: every <c>/dev/ttyACM*</c> / <c>/dev/ttyUSB*</c>
/// node has a <c>/sys/class/tty/&lt;name&gt;/device</c> symlink into the USB device
/// tree, whose enclosing device directory exposes <c>idVendor</c>,
/// <c>idProduct</c> and <c>serial</c>. Pure BCL file IO — AOT-safe, no P/Invoke.
/// All five HYTE hubs share VID 0x3402 and differ only by PID, so one helper
/// backs all of them; each device's thin wrapper maps the matched PID to its
/// own port-info shape.
/// </summary>
internal static class LinuxSerialDiscovery
{
    /// <summary>A serial port that matched the requested USB VID/PID filter.</summary>
    internal readonly record struct Match(string PortName, string Serial, int ProductId);

    /// <summary>
    /// Enumerate attached serial ports whose USB parent reports
    /// <paramref name="vendorId"/> and one of <paramref name="productIds"/>.
    /// Returns an empty list off-Linux (so the shared non-Windows compile unit
    /// stays happy on macOS) and on any IO error.
    /// </summary>
    internal static IReadOnlyList<Match> Find(int vendorId, params int[] productIds)
    {
        if (!OperatingSystem.IsLinux())
            return Array.Empty<Match>();

        var matches = new List<Match>();
        foreach (var devPath in EnumerateSerialNodes())
        {
            try
            {
                var name = Path.GetFileName(devPath); // e.g. "ttyACM0"
                var usbDir = ResolveUsbDeviceDir($"/sys/class/tty/{name}/device");
                if (usbDir is null)
                    continue;

                if (!TryReadHex(Path.Combine(usbDir, "idVendor"), out var vid) || vid != vendorId)
                    continue;
                if (!TryReadHex(Path.Combine(usbDir, "idProduct"), out var pid))
                    continue;
                if (productIds.Length > 0 && Array.IndexOf(productIds, pid) < 0)
                    continue;

                var serial = TryReadText(Path.Combine(usbDir, "serial")) ?? "";
                matches.Add(new Match(devPath, serial, pid));
            }
            catch
            {
                // A device can disappear mid-enumeration; skip it and keep going.
            }
        }
        return matches;
    }

    private static IEnumerable<string> EnumerateSerialNodes()
    {
        if (!Directory.Exists("/dev"))
            yield break;

        foreach (var pattern in new[] { "ttyACM*", "ttyUSB*" })
        {
            string[] entries;
            try { entries = Directory.GetFiles("/dev", pattern); }
            catch { entries = Array.Empty<string>(); }
            foreach (var e in entries)
                yield return e;
        }
    }

    /// <summary>
    /// Resolve the <c>/sys/class/tty/&lt;name&gt;/device</c> symlink to the real
    /// path, then walk up the USB device tree until a directory exposing
    /// <c>idVendor</c> is found. The <c>device</c> link points at the USB
    /// *interface* (e.g. <c>2-1:1.0</c>); the VID/PID/serial live on its parent
    /// USB *device* node (e.g. <c>2-1</c>), so we climb a few levels.
    /// </summary>
    private static string? ResolveUsbDeviceDir(string deviceSymlink)
    {
        string? dir;
        try
        {
            var target = Directory.ResolveLinkTarget(deviceSymlink, returnFinalTarget: true);
            if (target is null)
                return null; // not a symlink / unresolvable — don't walk the literal path
            dir = target.FullName;
        }
        catch
        {
            return null;
        }

        for (var i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
        {
            if (File.Exists(Path.Combine(dir, "idVendor")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private static bool TryReadHex(string path, out int value)
    {
        value = 0;
        var text = TryReadText(path);
        return text is not null && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static string? TryReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }
}
