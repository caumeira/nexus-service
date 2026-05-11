using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Qos.Service.Models.Lighting;
#if WINDOWS
using Vortice.DXGI;
#endif

namespace Qos.Service.Platform;

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
        return new List<ScreenSyncMonitor>();
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
