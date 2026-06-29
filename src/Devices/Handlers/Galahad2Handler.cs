using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Galahad2;

namespace Nexus.Service.Devices.Handlers;

public sealed class Galahad2Handler : IDeviceHandler
{
    private readonly Galahad2Hub _hub;

    public Galahad2Handler(Galahad2Hub hub)
    {
        _hub = hub;
        Identifiers = new[]
        {
            new UsbId(Galahad2Protocol.VendorId, Galahad2Protocol.ProductIdPerformance),
            new UsbId(Galahad2Protocol.VendorId, Galahad2Protocol.ProductIdRegular),
        };
    }

    public string Id => "lianli-aio";
    public string Name => "Lian Li Galahad II";
    public string Category => "cooler";

    public IReadOnlyList<UsbId> Identifiers { get; }

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        if (_hub.IsConnected)
        {
            return true;
        }
        return detectedDevices.Any(d =>
            d.VendorId == Galahad2Protocol.VendorId &&
            (d.ProductId == Galahad2Protocol.ProductIdPerformance ||
             d.ProductId == Galahad2Protocol.ProductIdRegular));
    }

    public string GetFirmwareVersion() => "";
}
