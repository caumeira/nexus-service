#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Nexus.Service.Devices.Detection.Native;

/// <summary>
/// P/Invoke declarations for CfgMgr32 (cfgmgr32.dll): present-device
/// enumeration, devnode property reads, and PnP device-interface change
/// notifications. AOT-friendly via LibraryImport.
/// </summary>
internal static partial class CfgMgr32
{
    internal const uint CR_SUCCESS = 0;

    private const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
    private const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;

    // devpropdef.h: DEVPROP_TYPE_STRING
    private const uint DevPropTypeString = 0x00000012;

    internal const int CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE = 0;
    internal const int CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL = 0;
    internal const int CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL = 1;

    /// <summary>usbiodef.h GUID_DEVINTERFACE_USB_DEVICE - hub-attached USB devices.</summary>
    internal static readonly Guid UsbDeviceInterfaceClass = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVPROPKEY
    {
        public Guid Fmtid;
        public uint Pid;

        public DEVPROPKEY(uint a, ushort b, ushort c, byte d, byte e, byte f, byte g, byte h, byte i, byte j, byte k, uint pid)
        {
            Fmtid = new Guid(a, b, c, d, e, f, g, h, i, j, k);
            Pid = pid;
        }
    }

    // devpkey.h
    internal static readonly DEVPROPKEY DEVPKEY_Device_DeviceDesc =
        new(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 2);
    internal static readonly DEVPROPKEY DEVPKEY_Device_Class =
        new(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 9);
    internal static readonly DEVPROPKEY DEVPKEY_Device_Manufacturer =
        new(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 13);
    internal static readonly DEVPROPKEY DEVPKEY_Device_LocationInfo =
        new(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 15);
    internal static readonly DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc =
        new(0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2, 4);
    internal static readonly DEVPROPKEY DEVPKEY_Device_DriverInfPath =
        new(0xa8b865dd, 0x2e3d, 0x4094, 0xad, 0x97, 0xe5, 0x93, 0xa7, 0x0c, 0x75, 0xd6, 5);

    /// <summary>
    /// cfgmgr32.h CM_NOTIFY_FILTER: 16-byte header + 400-byte union
    /// (WCHAR InstanceId[MAX_DEVICE_ID_LEN=200] is the largest member).
    /// Only the DeviceInterface.ClassGuid union member is used here.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    internal struct CM_NOTIFY_FILTER
    {
        [FieldOffset(0)] public uint cbSize;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public int FilterType;
        [FieldOffset(12)] public uint Reserved;
        [FieldOffset(16)] public Guid ClassGuid;
    }

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CM_Get_Device_ID_List_SizeW")]
    private static partial uint CM_Get_Device_ID_List_Size(out uint pulLen, string pszFilter, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CM_Get_Device_ID_ListW")]
    private static unsafe partial uint CM_Get_Device_ID_List(string pszFilter, char* buffer, uint bufferLen, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CM_Locate_DevNodeW")]
    internal static partial uint CM_Locate_DevNode(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static unsafe partial uint CM_Get_DevNode_Property(
        uint dnDevInst, in DEVPROPKEY propertyKey, out uint propertyType,
        byte* propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    internal static partial uint CM_Register_Notification(
        in CM_NOTIFY_FILTER pFilter, IntPtr pContext, IntPtr pCallback, out IntPtr pNotifyContext);

    [LibraryImport("cfgmgr32.dll")]
    internal static partial uint CM_Unregister_Notification(IntPtr notifyContext);

    internal static uint LocateDevNode(string instanceId, out uint devInst)
        => CM_Locate_DevNode(out devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL);

    /// <summary>
    /// Instance ids of currently-attached devices under the given enumerator
    /// (e.g. "USB"). The list can change between the size query and the fetch,
    /// so the fetch retries with a re-queried size. Empty list on failure.
    /// </summary>
    internal static unsafe List<string> GetPresentDeviceIds(string enumerator)
    {
        const uint flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_Size(out var len, enumerator, flags) != CR_SUCCESS || len == 0)
            {
                return new List<string>();
            }
            // Slack absorbs devices arriving between the size query and the fetch.
            var buffer = new char[len + 1024];
            fixed (char* p = buffer)
            {
                if (CM_Get_Device_ID_List(enumerator, p, (uint)buffer.Length, flags) == CR_SUCCESS)
                {
                    return SplitMultiSz(buffer);
                }
            }
        }
        return new List<string>();
    }

    private static List<string> SplitMultiSz(char[] buffer)
    {
        var result = new List<string>();
        var start = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0')
            {
                continue;
            }
            if (i == start)
            {
                break; // double null = end of list
            }
            result.Add(new string(buffer, start, i - start));
            start = i + 1;
        }
        return result;
    }

    /// <summary>
    /// String devnode property, or "" when absent / not a string / read error.
    /// </summary>
    internal static unsafe string GetStringProperty(uint devInst, in DEVPROPKEY key)
    {
        uint size = 0;
        CM_Get_DevNode_Property(devInst, in key, out _, null, ref size, 0);
        if (size == 0 || size > 64 * 1024)
        {
            return "";
        }
        var buffer = new byte[size];
        uint type;
        uint ret;
        fixed (byte* p = buffer)
        {
            ret = CM_Get_DevNode_Property(devInst, in key, out type, p, ref size, 0);
        }
        if (ret != CR_SUCCESS || type != DevPropTypeString)
        {
            return "";
        }
        // UTF-16, null-terminated.
        var text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, buffer.Length));
        var nul = text.IndexOf('\0');
        return nul >= 0 ? text.Substring(0, nul) : text;
    }
}
#endif
