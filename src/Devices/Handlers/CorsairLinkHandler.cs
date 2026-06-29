using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.CorsairLink;

namespace Nexus.Service.Devices.Handlers;

public sealed class CorsairLinkHandler : IDeviceHandler
{
    private readonly CorsairLinkHub _hub;

    public CorsairLinkHandler(CorsairLinkHub hub)
    {
        _hub = hub;
    }

    public string Id => "corsair";
    public string Name => "Corsair iCUE LINK";
    public string Category => "hub";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(CorsairLinkProtocol.VendorId, CorsairLinkProtocol.ProductId),
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        if (_hub.IsConnected) return true;
        return detectedDevices.Any(d =>
            Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));
    }

    public string GetFirmwareVersion() => _hub.State.Firmware;
}
