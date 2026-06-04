using System.Collections.Generic;

namespace Nexus.Service.Devices.Detection;

/// <summary>
/// Single source of truth for "is this device on the USB bus right now," used to
/// gate per-device background workers so they don't poll — and log — for hardware
/// that isn't attached. On a host with none of a given device, its worker would
/// otherwise re-run discovery every 2-3 s and emit a status line each tick.
///
/// Descriptor-queried, not a fixed device list: a query is a vendor id plus an
/// optional product-id set, matched against the live enumeration. A third-party
/// app's worker gates through the same call with its cert-granted VID/PIDs — the
/// enumeration "expands" for free because presence is a filter over whatever is on
/// the bus, not a hard-coded table. See plans/third-party-app-sdk.md
/// (PluginProcessSupervisor) for the intended plugin hook.
///
/// Backed by the shared 10 s-cached <see cref="IUsbEnumerator"/>, so a per-tick
/// presence check adds no bus-scan cost over the device detection already running.
/// Note: the monitor channel (<see cref="Platform.MonitorEnumerator"/>) carries no
/// EDID/vendor, so it cannot identify a specific product (e.g. a Y70) and is not a
/// gate source; a Y70 is gated on its serial controller's VID/PID instead.
/// </summary>
public sealed class HardwarePresence
{
    private readonly IUsbEnumerator _usb;

    public HardwarePresence(IUsbEnumerator usb) { _usb = usb; }

    /// <summary>
    /// True when a USB device with <paramref name="vendorId"/> is currently
    /// enumerated. When <paramref name="productIds"/> is non-empty the product id
    /// must also be one of them; an empty set matches any product under the vendor.
    /// </summary>
    public bool UsbPresent(int vendorId, params int[] productIds)
    {
        foreach (var d in _usb.Enumerate())
        {
            if (d.VendorId != vendorId)
                continue;
            if (productIds.Length == 0 || Contains(productIds, d.ProductId))
                return true;
        }
        return false;
    }

    private static bool Contains(IReadOnlyList<int> ids, int value)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            if (ids[i] == value)
                return true;
        }
        return false;
    }
}
