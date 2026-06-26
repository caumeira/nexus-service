using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LianLi;

namespace Nexus.Service.Devices.Handlers;

public sealed class LianLiHandler : IDeviceHandler
{
    private readonly LianLiHub _hub;

    public LianLiHandler(LianLiHub hub)
    {
        _hub = hub;
    }

    public string Id => "lianli";
    public string Name => "Lian Li Uni Hub SL-Infinity";
    public string Category => "hub";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(LianLiProtocol.VendorId, LianLiProtocol.ProductId),
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        if (_hub.IsConnected) return true;
        return detectedDevices.Any(d =>
            Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));
    }

    public string GetFirmwareVersion() => "";
}
