#if WINDOWS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// SetupAPI discovery for the SL-LCD Wireless screens. Enumerates
/// <see cref="Slv3LcdProtocol.LcdInterfaceGuid"/> device interfaces and
/// filters by VID 0x1CBE (PID 0x0005 SL-LCD or 0x0006 TL-LCD); see
/// plans/lianli-wireless-support.md section 4.4.
/// </summary>
public sealed class WindowsSlv3LcdDiscovery : ISlv3LcdDiscovery
{
    private static readonly Guid LcdGuid = new(Slv3LcdProtocol.LcdInterfaceGuid);

    public IReadOnlyList<Slv3LcdPortInfo> Discover()
    {
        var result = new List<Slv3LcdPortInfo>();
        var guid = LcdGuid;
        var devInfo = Slv3WinUsbInterop.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero,
            Slv3WinUsbInterop.DIGCF_PRESENT | Slv3WinUsbInterop.DIGCF_DEVICEINTERFACE);
        if (devInfo == (IntPtr)(-1))
        {
            return result;
        }

        try
        {
            var idx = 0u;
            var ifaceData = new Slv3WinUsbInterop.SP_DEVICE_INTERFACE_DATA();
            ifaceData.cbSize = Marshal.SizeOf(ifaceData);

            while (Slv3WinUsbInterop.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref guid, idx, ref ifaceData))
            {
                idx++;
                var path = ReadDevicePath(devInfo, ref ifaceData);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }
                if (!TryParseVidPid(path, out var vid, out var pid) || vid != Slv3LcdProtocol.VendorId
                    || (pid != Slv3LcdProtocol.ProductIdSl && pid != Slv3LcdProtocol.ProductIdTl))
                {
                    continue;
                }
                result.Add(new Slv3LcdPortInfo
                {
                    PortName = path,
                    Serial = ParseSerial(path),
                    ProductId = pid,
                });
            }
        }
        finally
        {
            Slv3WinUsbInterop.SetupDiDestroyDeviceInfoList(devInfo);
        }
        return result;
    }

    // Device paths look like \\?\USB#VID_1CBE&PID_0005#<serial>#{guid}.
    private static bool TryParseVidPid(string path, out int vid, out int pid)
    {
        vid = 0;
        pid = 0;
        var vidIdx = path.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        var pidIdx = path.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (vidIdx < 0 || pidIdx < 0 || vidIdx + 8 > path.Length || pidIdx + 8 > path.Length)
        {
            return false;
        }
        return int.TryParse(path.AsSpan(vidIdx + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid)
            && int.TryParse(path.AsSpan(pidIdx + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
    }

    private static string ParseSerial(string devicePath)
    {
        var parts = devicePath.Split('#');
        return parts.Length > 2 ? parts[2] : "";
    }

    private static string ReadDevicePath(IntPtr devInfo, ref Slv3WinUsbInterop.SP_DEVICE_INTERFACE_DATA ifaceData)
    {
        Slv3WinUsbInterop.SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData, IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required == 0)
        {
            return "";
        }

        var detail = Marshal.AllocHGlobal((int)required);
        try
        {
            // First 4 bytes are the size prefix; layout differs 32 vs 64 bit.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!Slv3WinUsbInterop.SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData, detail, required, out _, IntPtr.Zero))
            {
                return "";
            }
            return Marshal.PtrToStringUni(detail + 4) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }
}
#endif
