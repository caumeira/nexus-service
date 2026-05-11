using System.Collections.Generic;
using System.Linq;

namespace Qos.Service.Devices.Handlers;

/// <summary>
/// CNVS RGB controller — the main iBUYPOWER case lighting controller.
/// Multiple hardware revisions share the same handler.
/// </summary>
public sealed class CnvsHandler : IDeviceHandler
{
    private const int HyteVid = 0x3402;

    public string Id => "cnvs";
    public string Name => "CNVS";
    public string Category => "controller";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(HyteVid, 0x0BFF), // CNVS
        new UsbId(HyteVid, 0x0B00), // CNVS Left
        new UsbId(HyteVid, 0x0B01), // CNVS v1
        new UsbId(HyteVid, 0x0B02), // CNVS White
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));

    public string GetFirmwareVersion() => ""; // TODO: read via HID serial protocol
}
