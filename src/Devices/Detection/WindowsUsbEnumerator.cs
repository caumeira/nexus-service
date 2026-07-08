#if WINDOWS
using System;
using System.Collections.Generic;
using Nexus.Service.Devices.Detection.Native;

namespace Nexus.Service.Devices.Detection;

/// <summary>
/// Windows USB enumeration via CfgMgr32 (present devices under the "USB"
/// enumerator + per-devnode DEVPKEY_* property reads). In-process - no
/// pnputil.exe child process, no localized-text parsing. The property set
/// mirrors what `pnputil /enum-devices /connected /properties` carried:
/// DEVPKEY_Device_BusReportedDeviceDesc is the USB iProduct string (the
/// device's own brand name), with the driver's generic DeviceDesc as the
/// fallback name.
/// </summary>
public sealed class WindowsUsbEnumerator : IUsbEnumerator
{
    public List<UsbDeviceEntry> Enumerate()
    {
        try
        {
            return EnumeratePresent();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[usb-enum] Windows enumeration failed: {ex.Message}");
            return new List<UsbDeviceEntry>();
        }
    }

    private static List<UsbDeviceEntry> EnumeratePresent()
    {
        var result = new List<UsbDeviceEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var instanceId in CfgMgr32.GetPresentDeviceIds("USB"))
        {
            if (CfgMgr32.LocateDevNode(instanceId, out var devInst) != CfgMgr32.CR_SUCCESS)
            {
                continue;
            }

            UsbDeviceEntryBuilder.Append(
                result, seen, instanceId,
                busReportedDesc: CfgMgr32.GetStringProperty(devInst, in CfgMgr32.DEVPKEY_Device_BusReportedDeviceDesc),
                deviceDescription: CfgMgr32.GetStringProperty(devInst, in CfgMgr32.DEVPKEY_Device_DeviceDesc),
                manufacturer: CfgMgr32.GetStringProperty(devInst, in CfgMgr32.DEVPKEY_Device_Manufacturer),
                className: CfgMgr32.GetStringProperty(devInst, in CfgMgr32.DEVPKEY_Device_Class),
                driverName: CfgMgr32.GetStringProperty(devInst, in CfgMgr32.DEVPKEY_Device_DriverInfPath),
                locationInfo: CfgMgr32.GetStringProperty(devInst, in CfgMgr32.DEVPKEY_Device_LocationInfo));
        }
        return result;
    }
}
#endif
