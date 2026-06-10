using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hyte.SmartHub;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// HYTE SmartHub ARGB + PWM-fan hub. Connection state and firmware version
/// come from the singleton <see cref="SmartHubHub"/>; this handler is just the
/// integration point with <c>DeviceManager</c> / <c>DeviceBroadcaster</c>.
/// </summary>
public sealed class SmartHubHandler : IDeviceHandler
{
    private readonly SmartHubHub _hub;

    public SmartHubHandler(SmartHubHub hub)
    {
        _hub = hub;
    }

    public string Id => SmartHubHub.DeviceType;
    public string Name => SmartHubHub.ProductName;
    public string Category => "hub";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(SmartHubProtocol.VendorId, SmartHubProtocol.ProductId),
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        // Prefer the hub's live opinion (it has actually opened the serial
        // port) over USB enumeration. Mirrors Np50Handler / FanHubHandler.
        if (_hub.IsConnected) return true;
        return detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));
    }

    public string GetFirmwareVersion() => _hub.State.FirmwareVersion;
}
