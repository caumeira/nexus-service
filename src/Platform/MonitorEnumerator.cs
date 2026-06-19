using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Nexus.Service.Models.Lighting;
#if WINDOWS
using Vortice.DXGI;
#endif

namespace Nexus.Service.Platform;

public static class MonitorEnumerator
{
    public static List<ScreenSyncMonitor> List()
    {
#if WINDOWS
        var result = ListDxgi();
        if (result.Count > 0) return result;
#endif
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return ListAvFoundation();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return ListDrm();
        return new List<ScreenSyncMonitor>();
    }

    /// <summary>
    /// Enumerate connected displays from the DRM connector sysfs tree. Works for
    /// a root daemon with no graphical session attached - no X/Wayland, no DXGI -
    /// and covers every GPU/compositor. <c>/sys/class/drm/cardN-CONN/status</c> is
    /// "connected" for live outputs; the preferred mode (first line of <c>modes</c>)
    /// gives a resolution label. The connector name (e.g. <c>DP-1</c>) is the id.
    ///
    /// On Wayland this id is informational: the actual capture source is chosen in
    /// the desktop's screen-share picker (the PipeWire portal), which is the
    /// source of truth for which monitor gets mirrored.
    /// </summary>
    private static List<ScreenSyncMonitor> ListDrm()
    {
        var monitors = new List<ScreenSyncMonitor>();
        try
        {
            foreach (var dir in Directory.GetDirectories("/sys/class/drm", "card*-*"))
            {
                var statusFile = Path.Combine(dir, "status");
                if (!File.Exists(statusFile) ||
                    !string.Equals(File.ReadAllText(statusFile).Trim(), "connected", StringComparison.Ordinal))
                {
                    continue;
                }
                // Strip the leading "cardN-" to get the connector name, e.g.
                // "card1-DP-1" -> "DP-1", "card0-HDMI-A-1" -> "HDMI-A-1".
                var baseName = Path.GetFileName(dir);
                var dash = baseName.IndexOf('-');
                var connector = dash >= 0 ? baseName[(dash + 1)..] : baseName;

                string? mode = null;
                var modesFile = Path.Combine(dir, "modes");
                if (File.Exists(modesFile))
                {
                    var first = File.ReadLines(modesFile).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(first))
                        mode = first.Trim();
                }
                monitors.Add(new ScreenSyncMonitor
                {
                    Id = connector,
                    Name = mode is null ? connector : $"{connector} ({mode})",
                });
            }
            monitors.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        }
        catch (Exception ex) { Console.Error.WriteLine($"[monitor-enum] drm failed: {ex.Message}"); }
        return monitors;
    }

#if WINDOWS
    private static List<ScreenSyncMonitor> ListDxgi()
    {
        var monitors = new List<ScreenSyncMonitor>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            if (factory.EnumAdapters1(0u, out var adapter).Failure) return monitors;
            try
            {
                uint idx = 0;
                while (adapter.EnumOutputs(idx, out var output).Success)
                {
                    var desc = output.Description;
                    var w = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
                    var h = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
                    monitors.Add(new ScreenSyncMonitor { Id = idx.ToString(), Name = $"Display {idx + 1} ({w}\u00d7{h})" });
                    output.Dispose();
                    idx++;
                }
            }
            finally { adapter.Dispose(); }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[monitor-enum] DXGI failed: {ex.Message}"); }
        return monitors;
    }
#endif

    private static List<ScreenSyncMonitor> ListAvFoundation()
    {
        var monitors = new List<ScreenSyncMonitor>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/local/bin/ffmpeg",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("avfoundation");
            psi.ArgumentList.Add("-list_devices");
            psi.ArgumentList.Add("true");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add("");
            var proc = Process.Start(psi);
            if (proc is null)
                return monitors;
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);
            if (!proc.HasExited)
            { try { proc.Kill(); } catch { } }
            var screenNum = 1;
            foreach (var line in stderr.Split('\n'))
            {
                var match = Regex.Match(line, @"\[(\d+)\]\s+Capture screen \d+");
                if (match.Success)
                {
                    monitors.Add(new ScreenSyncMonitor
                    {
                        Id = match.Groups[1].Value,
                        Name = $"Display {screenNum}",
                    });
                    screenNum++;
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[monitor-enum] avfoundation failed: {ex.Message}"); }
        return monitors;
    }
}
