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
        Identifiers = new[]
        {
            new UsbId(KrakenProtocol.VendorId, KrakenProtocol.ProductIdKrakenEliteV2),
        };
    }

    public string Id => KrakenHub.DeviceId;
    public string Name => KrakenHub.ProductName;
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
            d.ProductId == KrakenProtocol.ProductIdKrakenEliteV2);
    }

    public string GetFirmwareVersion() => _hub.IsConnected ? _hub.Snapshot.FirmwareVersion : "";

    /// <summary>
    /// The LCD needs the WinUSB bulk pipe. Everything else (telemetry, fans, RGB) rides
    /// HID and still works without it, so this is a warning rather than a disconnect.
    /// </summary>
    public string? GetWarning(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        _hub.IsConnected && !_hub.HasLcd ? "lcd-unavailable" : null;
}
