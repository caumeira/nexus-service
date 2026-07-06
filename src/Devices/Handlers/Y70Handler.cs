using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hyte.Y70Display;
using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Devices.Handlers;

/// <summary>Y70 touch display - multiple panel variants (standard, Infinite, Truly).</summary>
public sealed class Y70Handler : IDeviceHandler
{
    private const int HyteVid = 0x3402;
    private const string UsbDisconnectedWarning = "usb-disconnected";

    private readonly Y70DisplayHub _hub;
    private readonly DisplayTopologyService _topology;

    public Y70Handler(Y70DisplayHub hub, DisplayTopologyService topology)
    {
        _hub = hub;
        _topology = topology;
    }

    public string Id => "y70";

    // Every panel variant surfaces under the family name; the specific variant
    // (Touch / Infinite / Truly) is carried by FirmwareType for OTA, not the label.
    public string Name => "Y70 Touch";

    public string Category => "display";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(HyteVid, 0x0C01), // HYTE Y70 Display (USB Serial Device - observed on test hardware)
        new UsbId(HyteVid, 0x0700), // Y70 Touch
        new UsbId(HyteVid, 0x0701), // Y70 Touch Infinite
        new UsbId(HyteVid, 0x0702), // Y70 Touch Truly
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        // Trust the display controller's live serial connection (it has opened
        // the COM port), then the cached topology's Y70 EDID match (the Y70 is
        // also a Windows display, reachable even with the serial channel
        // unplugged), then fall back to USB enumeration.
        if (_hub.IsConnected) return true;
        if (_topology.HasY70Display()) return true;
        return detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));
    }

    /// <summary>"usb-disconnected" when the monitor is present but the serial
    /// control channel (brightness/screen-power/touch) is not; null otherwise.</summary>
    public string? GetWarning(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        if (_hub.IsConnected) return null;
        return _topology.HasY70Display() ? UsbDisconnectedWarning : null;
    }

    public string GetFirmwareVersion() => _hub.State.FirmwareVersion;

    // "y70" / "y70-infinite" / "y70-truly" once the controller reports its
    // variant; Id ("y70", no bundled firmware) until then so the device isn't
    // offered an update before we know which image applies.
    public string FirmwareType => string.IsNullOrEmpty(_hub.Variant) ? Id : _hub.Variant;
}
