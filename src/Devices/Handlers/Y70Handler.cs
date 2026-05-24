using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Devices.Handlers;

/// <summary>Y70 touch display — multiple panel variants (standard, Infinite, Truly).</summary>
public sealed class Y70Handler : IDeviceHandler
{
    private const int HyteVid = 0x3402;

    public string Id => "y70";
    public string Name => "Y70 Touch";
    public string Category => "display";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(HyteVid, 0x0C01), // HYTE Y70 Display (USB Serial Device — observed on test hardware)
        new UsbId(HyteVid, 0x0700), // Y70 Touch
        new UsbId(HyteVid, 0x0701), // Y70 Touch Infinite
        new UsbId(HyteVid, 0x0702), // Y70 Touch Truly
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));

    public string GetFirmwareVersion() => "";
}
