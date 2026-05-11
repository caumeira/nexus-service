using System;
using System.Collections.Generic;
using System.Linq;
using Qos.Service.Devices;
using Qos.Service.Devices.Detection;
using Qos.Service.Peripherals.Hid;
using Qos.Service.Peripherals.Protocols.Corsair;
using Qos.Service.Peripherals.Protocols.Razer;

namespace Qos.Service.Peripherals;

/// <summary>
/// Owns the live set of third-party peripherals detected on the system. Polls the
/// existing USB enumerator on demand; for each matching VID/PID it delegates to the
/// appropriate vendor factory (Razer today; Logitech/Corsair to follow).
///
/// Instances are cached by (VID, PID, Serial) to keep HID handles open across requests.
/// Unplugged devices are closed and removed on the next snapshot that omits them.
/// </summary>
public sealed class PeripheralRegistry : IDisposable
{
    private readonly IUsbEnumerator _usb;
    private readonly IHidEnumerator _hid;
    private readonly RazerPeripheralFactory _razer = new();
    private readonly CorsairPeripheralFactory _corsair = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, IPeripheral> _cache = new();

    public PeripheralRegistry(IUsbEnumerator usb, IHidEnumerator hid)
    {
        _usb = usb;
        _hid = hid;
    }

    /// <summary>Returns the current set of detected peripherals, refreshing from USB on each call.</summary>
    public IReadOnlyList<IPeripheral> GetAll()
    {
        lock (_lock)
        {
            Refresh();
            return _cache.Values.ToList();
        }
    }

    public IPeripheral? Get(string id)
    {
        lock (_lock)
        {
            Refresh();
            // id is the logical peripheral id (e.g. "corsair-m65-pro-rgb-9&..."), not the cache key.
            foreach (var p in _cache.Values)
            {
                if (p.Id == id)
                {
                    return p;
                }
            }
            return null;
        }
    }

    private void Refresh()
    {
        var usbDevices = _usb.Enumerate();
        var seen = new HashSet<string>();

        foreach (var usb in usbDevices)
        {
            // Deterministic cache key per USB-detected device. Gate peripheral creation on
            // this so we only open HID handles once per device lifetime, not per refresh.
            var cacheKey = $"{usb.VendorId:X4}:{usb.ProductId:X4}:{usb.Serial ?? ""}";

            if (_cache.TryGetValue(cacheKey, out var existing))
            {
                seen.Add(cacheKey);
                continue;
            }

            IPeripheral? peripheral = null;

            if (usb.VendorId == RazerPeripheralFactory.RazerVendorId && RazerPeripheralFactory.Supports(usb.ProductId))
            {
                peripheral = _razer.TryCreate(_hid, usb.ProductId, usb.Serial ?? "");
            }
            else if (usb.VendorId == CorsairPeripheralFactory.CorsairVendorId && CorsairPeripheralFactory.Supports(usb.ProductId))
            {
                peripheral = _corsair.TryCreate(_hid, usb.ProductId, usb.Serial ?? "");
            }
            // TODO: Logitech HID++ factory, etc.

            if (peripheral is not null)
            {
                _cache[cacheKey] = peripheral;
                seen.Add(cacheKey);
            }
        }

        // Drop peripherals no longer present.
        foreach (var stale in _cache.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            if (_cache.Remove(stale, out var p) && p is IDisposable d)
            {
                try
                { d.Dispose(); }
                catch { }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var p in _cache.Values)
            {
                if (p is IDisposable d)
                { try { d.Dispose(); } catch { } }
            }
            _cache.Clear();
        }
    }
}
