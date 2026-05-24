using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Lifecycle.Native;

/// <summary>
/// P/Invoke declarations for SetupAPI (setupapi.dll) and newdev.dll. Used by
/// PawnIoInstaller to create the root-enumerated PnP device that pnputil can't
/// create on its own. AOT-friendly via LibraryImport.
///
/// The flow for installing a root-enumerated device like PawnIO:
///   1. SetupDiCreateDeviceInfoList — empty info set for the device class
///   2. SetupDiCreateDeviceInfo — add a new device entry with generated ID
///   3. SetupDiSetDeviceRegistryProperty(SPDRP_HARDWAREID) — set hardware ID
///   4. SetupDiCallClassInstaller(DIF_REGISTERDEVICE) — register device with PnP
///   5. UpdateDriverForPlugAndPlayDevices — install the driver from the INF
/// </summary>
internal static partial class SetupApi
{
    internal const uint DICD_GENERATE_ID = 0x00000001;
    internal const uint SPDRP_HARDWAREID = 0x00000001;
    internal const uint DIF_REGISTERDEVICE = 0x00000019;
    internal const uint DIF_REMOVE = 0x00000005;
    internal const uint INSTALLFLAG_FORCE = 0x00000001;

    internal static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [LibraryImport("setupapi.dll", SetLastError = true)]
    internal static partial IntPtr SetupDiCreateDeviceInfoList(in Guid classGuid, IntPtr hwndParent);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [LibraryImport("setupapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16, EntryPoint = "SetupDiCreateDeviceInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiCreateDeviceInfo(
        IntPtr deviceInfoSet,
        string deviceName,
        in Guid classGuid,
        string? deviceDescription,
        IntPtr hwndParent,
        uint creationFlags,
        ref SP_DEVINFO_DATA deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiSetDeviceRegistryPropertyW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiSetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        uint property,
        byte[] propertyBuffer,
        uint propertyBufferSize);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiCallClassInstaller(
        uint installFunction,
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData);

    [LibraryImport("newdev.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16, EntryPoint = "UpdateDriverForPlugAndPlayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateDriverForPlugAndPlayDevices(
        IntPtr hwndParent,
        string hardwareId,
        string fullInfPath,
        uint installFlags,
        [MarshalAs(UnmanagedType.Bool)] ref bool rebootRequired);
}
