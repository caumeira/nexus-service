using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Nzxt;

namespace Nexus.Service.Devices.Handlers;

public sealed class KrakenHandler : IDeviceHandler
{
    private readonly KrakenHub _hub;

    public KrakenHandler(KrakenHub hub)
    {
        _hub = hub;
        var ids = new UsbId[KrakenModel.All.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = new UsbId(KrakenProtocol.VendorId, KrakenModel.All[i].ProductId);
        }
        Identifiers = ids;
    }

    public string Id => KrakenHub.DeviceId;
    // Names the attached model once one is connected; the generic name is what the device
    // list shows for a cooler that is present but not yet talking.
    public string Name => _hub.IsConnected ? _hub.ModelName : KrakenHub.ProductName;
    public string Category => "cooler";

    public IReadOnlyList<UsbId> Identifiers { get; }

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        if (_hub.IsConnected)
        {
            return true;
        }
        return detectedDevices.Any(d =>
            d.VendorId == KrakenProtocol.VendorId &&
            KrakenProtocol.ProductIds.Contains(d.ProductId));
    }

    public string GetFirmwareVersion() => _hub.IsConnected ? _hub.Snapshot.FirmwareVersion : "";

    /// <summary>
    /// The LCD needs the WinUSB bulk pipe. Everything else (telemetry, fans, RGB) rides
    /// HID and still works without it, so this is a warning rather than a disconnect.
    /// </summary>
    public string? GetWarning(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        _hub.IsConnected && _hub.Model.HasLcd && !_hub.HasLcd ? "lcd-unavailable" : null;
}
