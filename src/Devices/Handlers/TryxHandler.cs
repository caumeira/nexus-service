using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Tryx.Panorama;

namespace Nexus.Service.Devices.Handlers;

/// <summary>Tryx Panorama AIO screen. Current firmware enumerates as 391A:1011
/// ("RK PANO"); control over its raw-USB-bulk interface is not yet implemented,
/// so this handler reports presence only.</summary>
public sealed class TryxHandler : IDeviceHandler
{
    public string Id => "tryx";
    public string Name => "Tryx Panorama";
    public string Category => "cooler";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(TryxPanoramaProtocol.VendorIdRk, TryxPanoramaProtocol.ProductIdPanoramaRk),
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));

    public string GetFirmwareVersion() => "";
}
