using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.CorsairLink;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// Device-list row for the iCUE LINK cooler's LCD screen module, which enumerates as its
/// own HID interface behind the hub. Separate from <see cref="CorsairLinkHandler"/> so the
/// glass has its own Nexus Control switch: claiming the hub for fans and RGB must not
/// silently replace whatever the vendor app is showing on someone's pump.
///
/// <see cref="HasPage"/> is false: the panel record is this device's page, the way a
/// streamed Kraken panel claims its cooler row.
/// </summary>
public sealed class CorsairLinkLcdHandler : IDeviceHandler
{
    private readonly CorsairLinkLcd _lcd;

    public CorsairLinkLcdHandler(CorsairLinkLcd lcd)
    {
        _lcd = lcd;
    }

    public string Id => CorsairLinkLcd.DeviceId;
    public string Name => "Corsair iCUE LINK LCD";
    public string Category => "cooler";
    public bool HasPage => false;

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(CorsairLinkLcd.LcdVendorId, CorsairLinkLcd.AioPid),
        new UsbId(CorsairLinkLcd.LcdVendorId, CorsairLinkLcd.Xd5Pid),
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        if (_lcd.HasDevice) return true;
        return detectedDevices.Any(d =>
            Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));
    }

    /// <summary>The LCD interface exposes no firmware query; the hub row carries the version.</summary>
    public string GetFirmwareVersion() => "";
}
