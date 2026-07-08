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
    // Throws on enumeration failure: CachingUsbEnumerator serves its last
    // known-good list instead of caching a false "bus empty" verdict that
    // would gate off every presence-checked heartbeat worker.
    public List<UsbDeviceEntry> Enumerate()
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
