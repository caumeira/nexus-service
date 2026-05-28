using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hyte.Y70Display;

namespace Nexus.Service.Devices.Handlers;

/// <summary>Y70 touch display — multiple panel variants (standard, Infinite, Truly).</summary>
public sealed class Y70Handler : IDeviceHandler
{
    private const int HyteVid = 0x3402;

    private readonly Y70DisplayHub _hub;

    public Y70Handler(Y70DisplayHub hub)
    {
        _hub = hub;
    }

    public string Id => "y70";

    // Variant-aware label once the controller reports which panel is attached.
    public string Name => _hub.Variant switch
    {
        Y70DisplayProtocol.VariantInfinite => "Y70 Touch Infinite",
        Y70DisplayProtocol.VariantTruly => "Y70 Touch Truly",
        _ => "Y70 Touch",
    };

    public string Category => "display";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(HyteVid, 0x0C01), // HYTE Y70 Display (USB Serial Device — observed on test hardware)
        new UsbId(HyteVid, 0x0700), // Y70 Touch
        new UsbId(HyteVid, 0x0701), // Y70 Touch Infinite
        new UsbId(HyteVid, 0x0702), // Y70 Touch Truly
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        // Trust the display controller's live serial connection (it has opened
        // the COM port), then fall back to USB enumeration.
        if (_hub.IsConnected) return true;
        return detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));
    }

    public string GetFirmwareVersion() => _hub.State.FirmwareVersion;

    // "y70" / "y70-infinite" / "y70-truly" once the controller reports its
    // variant; Id ("y70", no bundled firmware) until then so the device isn't
    // offered an update before we know which image applies.
    public string FirmwareType => string.IsNullOrEmpty(_hub.Variant) ? Id : _hub.Variant;
}
