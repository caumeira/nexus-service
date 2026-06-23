#if LINUX
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Nexus.Service.Panel;
using Nexus.Service.Platform.Linux;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>
/// Linux sysfs-based discovery for Tryx Panorama (VID 0x18D1). Reads
/// idVendor/idProduct/serial from /sys/class/tty for each ttyACM/ttyUSB node.
/// Also resolves the ADB serial via <c>adb devices -l</c>.
/// Returns empty off-Linux.
/// </summary>
public sealed class LinuxTryxPanoramaPortDiscovery : ITryxPanoramaPanelDiscovery
{
    public IReadOnlyList<TryxPanoramaPortInfo> Discover()
    {
        if (!OperatingSystem.IsLinux()) return Array.Empty<TryxPanoramaPortInfo>();

        var matches = new List<TryxPanoramaPortInfo>();
        var adbSerial = ResolveAdbSerial();

        foreach (var devPath in EnumerateSerialNodes("/dev"))
        {
            try
            {
                var name = Path.GetFileName(devPath);
                var usbDir = ResolveUsbDeviceDir(Path.Combine("/sys/class/tty", name, "device"));
                if (usbDir is null) continue;

                if (LinuxSysfs.ReadHex(Path.Combine(usbDir, "idVendor")) != TryxPanoramaProtocol.VendorId) continue;
                var pid = LinuxSysfs.ReadHex(Path.Combine(usbDir, "idProduct"));
                if (pid is null) continue;
                if (Array.IndexOf(TryxPanoramaProtocol.KnownProductIds, pid.Value) < 0) continue;

                var serial = LinuxSysfs.ReadText(Path.Combine(usbDir, "serial")) ?? "";
                matches.Add(new TryxPanoramaPortInfo
                {
                    PortName = devPath,
                    Serial = serial,
                    ProductId = pid.Value,
                    AdbSerial = adbSerial,
                });
            }
            catch { /* device disappeared mid-enumeration */ }
        }
        return matches;
    }

    private static IEnumerable<string> EnumerateSerialNodes(string devRoot)
    {
        if (!Directory.Exists(devRoot)) yield break;
        foreach (var pattern in new[] { "ttyACM*", "ttyUSB*" })
        {
            string[] entries;
            try { entries = Directory.GetFiles(devRoot, pattern); }
            catch { entries = Array.Empty<string>(); }
            foreach (var e in entries)
            {
                yield return e;
            }
        }
    }

    private static string? ResolveUsbDeviceDir(string deviceSymlink)
    {
        string? dir;
        try
        {
            var target = Directory.ResolveLinkTarget(deviceSymlink, returnFinalTarget: true);
            if (target is null) return null;
            dir = target.FullName;
        }
        catch { return null; }

        for (var i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
        {
            if (File.Exists(Path.Combine(dir, "idVendor"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private static string ResolveAdbSerial()
    {
        var adbPath = AdbLocator.ResolveAdbPath();
        if (adbPath is null) return "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = "devices -l",
                WorkingDirectory = Path.GetDirectoryName(adbPath) ?? string.Empty,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return "";
            p.WaitForExit(5_000);
            var output = p.StandardOutput.ReadToEnd();
            foreach (var line in output.Split('\n'))
            {
                if (line.IndexOf("product:cm01", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("model:cm01", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0) return parts[0];
                }
            }
        }
        catch { /* adb not found or not running */ }
        return "";
    }
}
#endif
