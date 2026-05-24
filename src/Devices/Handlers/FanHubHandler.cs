using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Devices.Handlers;

/// <summary>iBUYPOWER fan and ARGB hubs.</summary>
public sealed class FanHubHandler : IDeviceHandler
{
    private const int HyteVid = 0x3402;

    public string Id => "fan-hub";
    public string Name => "Fan Hub";
    public string Category => "hub";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(HyteVid, 0x0A00), // IBP Mini Hub
        new UsbId(HyteVid, 0x0A04), // PWM Fan + ARGB Hub
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));

    public string GetFirmwareVersion() => "";
}
