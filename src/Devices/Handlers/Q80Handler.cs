using System.Collections.Generic;
using System.Linq;

namespace Qos.Service.Devices.Handlers;

/// <summary>Q80 LCD case display.</summary>
public sealed class Q80Handler : IDeviceHandler
{
    private const int HyteVid = 0x3402;

    public string Id => "q80";
    public string Name => "Q80";
    public string Category => "display";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(HyteVid, 0x0603),
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));

    public string GetFirmwareVersion() => "";
}
