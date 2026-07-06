using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Tryx.Panorama;

namespace Nexus.Service.Devices.Handlers;

/// <summary>Tryx Panorama AIO screen. RK firmware enumerates under VID 391A
/// across the Panorama family (base "RK PANO", SE "PASE", WaterBlock, v2);
/// control over its raw-USB-bulk interface is not yet implemented, so this
/// handler reports presence only.</summary>
public sealed class TryxHandler : IDeviceHandler
{
    public string Id => "tryx";
    public string Name => "Tryx Panorama";
    public string Category => "cooler";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(TryxPanoramaProtocol.VendorIdRk, TryxPanoramaProtocol.ProductIdPanoramaRk),
        new UsbId(TryxPanoramaProtocol.VendorIdRk, TryxPanoramaProtocol.ProductIdPanoramaRkSe),
        new UsbId(TryxPanoramaProtocol.VendorIdRk, TryxPanoramaProtocol.ProductIdPanoramaRkSe2),
        new UsbId(TryxPanoramaProtocol.VendorIdRk, TryxPanoramaProtocol.ProductIdPanoramaRkWb),
        new UsbId(TryxPanoramaProtocol.VendorIdRk, TryxPanoramaProtocol.ProductIdPanoramaRkWb2),
        new UsbId(TryxPanoramaProtocol.VendorIdRk, TryxPanoramaProtocol.ProductIdPanoramaRkV2),
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));

    public string GetFirmwareVersion() => "";
}
