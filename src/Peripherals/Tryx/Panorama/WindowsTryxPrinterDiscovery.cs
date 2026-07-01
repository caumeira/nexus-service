#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>
/// SetupAPI discovery for the RK-firmware Panorama (VID 0x391A, "RK PANO"), which
/// binds to the usbprint device class rather than CDC serial. Enumerates
/// GUID_DEVINTERFACE_USBPRINT device interfaces and matches the VID substring in
/// the resolved device path; no driver rebind is needed.
/// </summary>
public sealed class WindowsTryxPrinterDiscovery : ITryxPanoramaPanelDiscovery
{
    private static readonly Guid UsbPrintInterfaceGuid = new("28d78fad-5a12-11d1-ae5b-0000f803a8c2");
    private const string VidFragment = "VID_391A";

    public IReadOnlyList<TryxPanoramaPortInfo> Discover()
    {
        var result = new List<TryxPanoramaPortInfo>();
        var devInfo = Native.SetupDiGetClassDevs(ref UsbPrintInterfaceGuidLocal(), null, IntPtr.Zero,
            Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (devInfo == (IntPtr)(-1))
        {
            return result;
        }

        try
        {
            var idx = 0u;
            var ifaceData = new Native.SP_DEVICE_INTERFACE_DATA();
            ifaceData.cbSize = Marshal.SizeOf(ifaceData);

            while (Native.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref UsbPrintInterfaceGuidLocal(), idx, ref ifaceData))
            {
                idx++;

                var devicePath = ReadDevicePath(devInfo, ref ifaceData);
                if (string.IsNullOrEmpty(devicePath)) continue;
                if (devicePath.IndexOf(VidFragment, StringComparison.OrdinalIgnoreCase) < 0) continue;

                result.Add(new TryxPanoramaPortInfo
                {
                    PortName = devicePath,
                    Serial = ParseSerial(devicePath),
                    ProductId = TryxPanoramaProtocol.ProductIdPanoramaRk,
                });
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(devInfo);
        }
        return result;
    }

    // Device paths look like \\?\USB#VID_391A&PID_1011#<serial>#{28d78fad-...}.
    private static string ParseSerial(string devicePath)
    {
        var parts = devicePath.Split('#');
        return parts.Length > 2 ? parts[2] : "";
    }

    private static string ReadDevicePath(IntPtr devInfo, ref Native.SP_DEVICE_INTERFACE_DATA ifaceData)
    {
        Native.SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData, IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required == 0) return "";

        var detail = Marshal.AllocHGlobal((int)required);
        try
        {
            // First 4 bytes are the size prefix; layout differs 32 vs 64 bit.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!Native.SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData, detail, required, out _, IntPtr.Zero))
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

    private static ref Guid UsbPrintInterfaceGuidLocal()
        => ref System.Runtime.CompilerServices.Unsafe.AsRef(in UsbPrintInterfaceGuid);

    private static class Native
    {
        public const uint DIGCF_PRESENT = 0x02;
        public const uint DIGCF_DEVICEINTERFACE = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr devInfo, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
            uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr devInfo, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr detailData,
            uint detailDataSize, out uint requiredSize, IntPtr deviceInfoData);
    }
}
#endif
