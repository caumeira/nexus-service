using System.Collections.Generic;
using System.Linq;

namespace Qos.Service.Devices.Handlers;

/// <summary>
/// HYTE Q-series AIO LCD displays (Q60 and Q80). The two variants share
/// hardware behavior and the same on-device runtime; only the USB PID
/// differs, so they map to the same handler.
/// </summary>
public sealed class QSeriesHandler : IDeviceHandler
{
    private const int HyteVid = 0x3402;

    public string Id => "qseries";
    public string Name => "Q-series";
    public string Category => "display";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(HyteVid, 0x0600), // Q60
        new UsbId(HyteVid, 0x0603), // Q80
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));

    public string GetFirmwareVersion() => "";
}
