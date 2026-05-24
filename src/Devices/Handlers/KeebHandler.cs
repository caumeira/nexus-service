using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Devices.Handlers;

/// <summary>HYTE keyboards (MK series).</summary>
public sealed class KeebHandler : IDeviceHandler
{
    private const int HyteVid = 0x3402;

    public string Id => "keeb";
    public string Name => "Keeb";
    public string Category => "keyboard";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(HyteVid, 0x0300), // Keeb TKL (Suoai)
        new UsbId(HyteVid, 0x0301), // Redragon Keeb
        new UsbId(HyteVid, 0x0303), // MK9 Keyboard
        new UsbId(HyteVid, 0x0304), // MK9 Pro
        new UsbId(HyteVid, 0x0305), // Redragon KM10 Keeb
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));

    public string GetFirmwareVersion() => "";
}
