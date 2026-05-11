using System.Collections.Generic;
using System.Linq;

namespace Qos.Service.Devices.Handlers;

/// <summary>Q60 LCD case display.</summary>
public sealed class Q60Handler : IDeviceHandler
{
    private const int HyteVid = 0x3402;

    public string Id => "q60";
    public string Name => "Q60";
    public string Category => "display";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(HyteVid, 0x0600),
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));

    public string GetFirmwareVersion() => "";
}
