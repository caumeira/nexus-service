using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LianLiTl;

namespace Nexus.Service.Devices.Handlers;

public sealed class LianLiTlHandler : IDeviceHandler
{
    private readonly TlFanHub _hub;

    public LianLiTlHandler(TlFanHub hub)
    {
        _hub = hub;
        Identifiers = new[] { new UsbId(TlFanProtocol.VendorId, TlFanProtocol.ProductId) };
    }

    public string Id => "lianli-tl";
    public string Name => "Lian Li Uni Fan TL";
    public string Category => "hub";

    public IReadOnlyList<UsbId> Identifiers { get; }

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        if (_hub.IsConnected)
        {
            return true;
        }
        return detectedDevices.Any(d =>
            d.VendorId == TlFanProtocol.VendorId && d.ProductId == TlFanProtocol.ProductId);
    }

    public string GetFirmwareVersion() => "";
}
