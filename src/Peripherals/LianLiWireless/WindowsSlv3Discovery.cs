#if WINDOWS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// SetupAPI discovery for the SLV3 TX/RX dongles. Enumerates
/// <see cref="Slv3Protocol.DongleInterfaceGuid"/> device interfaces and resolves
/// each path's role from its VID/PID (the Nuvoton pair or the WCH alias for the
/// same physical controller).
/// </summary>
public sealed class WindowsSlv3Discovery : ISlv3Discovery
{
    private static readonly Guid DongleGuid = new(Slv3Protocol.DongleInterfaceGuid);

    public IReadOnlyList<Slv3PortInfo> Discover()
    {
        var result = new List<Slv3PortInfo>();
        var guid = DongleGuid;
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
                if (!TryParseVidPid(path, out var vid, out var pid) || !TryResolveRole(vid, pid, out var role))
                {
                    continue;
                }
                result.Add(new Slv3PortInfo
                {
                    PortName = path,
                    Serial = ParseSerial(path),
                    Role = role,
                });
            }
        }
        finally
        {
            Slv3WinUsbInterop.SetupDiDestroyDeviceInfoList(devInfo);
        }
        return result;
    }

    private static bool TryResolveRole(int vid, int pid, out Slv3DongleRole role)
    {
        if ((vid == Slv3Protocol.TxVendorId && pid == Slv3Protocol.TxProductId)
            || (vid == Slv3Protocol.WchVendorId && pid == Slv3Protocol.TxProductIdWch))
        {
            role = Slv3DongleRole.Tx;
            return true;
        }
        if ((vid == Slv3Protocol.RxVendorId && pid == Slv3Protocol.RxProductId)
            || (vid == Slv3Protocol.WchVendorId && pid == Slv3Protocol.RxProductIdWch))
        {
            role = Slv3DongleRole.Rx;
            return true;
        }
        role = default;
        return false;
    }

    // Device paths look like \\?\USB#VID_0416&PID_8040#<serial>#{guid}.
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
