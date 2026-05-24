#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Nexus.Service.Devices.Detection;

/// <summary>
/// Windows USB enumeration via `pnputil /enum-devices /connected /properties`.
/// pnputil reports ONLY presently-attached devices (the HKLM\...\Enum\USB registry
/// subtree, used by an earlier implementation, is historical and lists every device
/// ever seen). The `/properties` flag makes pnputil include DEVPKEY_* fields so we
/// can pull DEVPKEY_Device_BusReportedDeviceDesc (the USB iProduct string — the
/// device's own brand name) and DEVPKEY_Device_LocationInfo in a single call.
/// </summary>
public sealed class WindowsUsbEnumerator : IUsbEnumerator
{
    public List<UsbDeviceEntry> Enumerate()
    {
        try
        {
            var output = ShellOut("pnputil", "/enum-devices", "/connected", "/properties");
            return PnpUtilParser.Parse(output);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[usb-enum] Windows enumeration failed: {ex.Message}");
            return new List<UsbDeviceEntry>();
        }
    }

    private static string ShellOut(string fileName, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return "";
            }
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);
            return output;
        }
        catch
        {
            return "";
        }
    }
}
#endif
