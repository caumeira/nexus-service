using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Peripherals.Protocols.Corsair;

/// <summary>
/// Maps Corsair VID/PID pairs to device metadata. Every entry in
/// <see cref="Models"/> has <c>HasProtocol = false</c>, so
/// <see cref="TryCreate"/> always returns a detection-only
/// <see cref="CorsairPeripheral"/>. The protocol branch handles "Bragi"-
/// generation mice that use HID feature reports.
/// </summary>
public sealed class CorsairPeripheralFactory
{
    public const int CorsairVendorId = 0x1B1C;

    // Corsair M65 Pro and friends use USB vendor control transfers
    // (bRequestType=0x40), not HID feature reports. Configuring them needs the
    // Windows HID driver replaced with a WinUSB filter (Zadig-style), which
    // breaks the mouse's normal operation; hence detection-only. "Bragi"-
    // protocol mice (e.g. Scimitar Elite Bragi) do use HID reports.
    private static readonly Dictionary<int, (string Name, string Category, bool HasProtocol)> Models = new()
    {
        // Mice - all detection-only on Windows without Zadig-style driver replacement
        [0x1B2E] = ("M65 Pro RGB", "mouse", false),
        [0x1B5A] = ("M65 RGB Elite", "mouse", false),
        [0x1B4C] = ("Dark Core RGB Pro", "mouse", false),
        [0x1B3E] = ("Scimitar RGB Elite", "mouse", false),
        [0x1B8C] = ("Sabre RGB Pro Wireless", "mouse", false),
        [0x1B6E] = ("Harpoon RGB Wireless", "mouse", false),
        [0x1B94] = ("Ironclaw RGB Wireless", "mouse", false),

        // Keyboards (detection-only)
        [0x1B6D] = ("K70 RGB Pro", "keyboard", false),
        [0x1B49] = ("K70 RGB MK.2", "keyboard", false),
        [0x1B2D] = ("K95 RGB Platinum", "keyboard", false),
        [0x1B7D] = ("K100 RGB", "keyboard", false),
        [0x1B33] = ("K68 RGB", "keyboard", false),

        // Headsets
        [0x1B88] = ("HS80 RGB Wireless", "headset", false),
        [0x0A6B] = ("Virtuoso RGB Wireless", "headset", false),
    };

    public static bool Supports(int pid) => Models.ContainsKey(pid);

    public IPeripheral? TryCreate(IHidEnumerator hid, int productId, string serial)
    {
        if (!Models.TryGetValue(productId, out var info))
        {
            return null;
        }

        if (info.HasProtocol && info.Category == "mouse")
        {
            // Pick the control interface: Corsair mice expose the 65-byte feature-report
            // interface on MI_01. Select the one with the largest feature-report size.
            var ifaces = hid.Find(CorsairVendorId, productId);
            var control = ifaces.OrderByDescending(i => i.FeatureReportByteLength).FirstOrDefault();
            if (control is not null && control.FeatureReportByteLength > 0)
            {
                var device = hid.Open(control.Path);
                if (device is not null)
                {
                    var client = new CorsairClient(device);
                    return new CorsairMousePeripheral(client, CorsairVendorId, productId, info.Name, serial);
                }
            }
        }

        return new CorsairPeripheral(CorsairVendorId, productId, info.Name, info.Category, serial);
    }
}
