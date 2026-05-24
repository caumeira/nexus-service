using System.Collections.Generic;

namespace Nexus.Service.Devices.Detection;

/// <summary>Stub enumerator that returns no devices. Used when no real USB access is available.</summary>
public sealed class StubUsbEnumerator : IUsbEnumerator
{
    public List<UsbDeviceEntry> Enumerate() => new();
}
