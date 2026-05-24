using System.Collections.Generic;

namespace Nexus.Service.Devices.Detection;

/// <summary>
/// Cross-platform USB device enumeration.
/// Implementations discover connected USB devices and return their VID/PID.
/// </summary>
public interface IUsbEnumerator
{
    List<UsbDeviceEntry> Enumerate();
}
