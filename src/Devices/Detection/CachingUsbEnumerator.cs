using System;
using System.Collections.Generic;

namespace Nexus.Service.Devices.Detection;

/// <summary>
/// Short-TTL cache over any <see cref="IUsbEnumerator"/>. On Windows the
/// underlying pnputil enumeration routinely costs 500-1500 ms and allocates
/// ~1 MB of parsed-string garbage; the Devices tab polls /devices/all and
/// /devices/usb/all every 5 s, and PeripheralRegistry.Refresh runs on its
/// own schedule too. Without this decorator every poll spawns fresh pnputil.
///
/// 10 s TTL means the 5 s poll cadence hits cache roughly every other call
/// -- pnputil cost halves without making hotplug detection feel slow (a USB
/// device plugged in appears within 10-15 s worst-case, which already matches
/// the polling cadence of peripheral battery reads).
/// </summary>
internal sealed class CachingUsbEnumerator : IUsbEnumerator
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    private readonly IUsbEnumerator _inner;
    private readonly object _lock = new();
    private List<UsbDeviceEntry>? _cached;
    private DateTime _cachedAt;

    public CachingUsbEnumerator(IUsbEnumerator inner) { _inner = inner; }

    public List<UsbDeviceEntry> Enumerate()
    {
        lock (_lock)
        {
            if (_cached is not null && DateTime.UtcNow - _cachedAt < CacheTtl)
            {
                return _cached;
            }
        }

        // Run outside the lock so a slow pnputil doesn't block concurrent
        // readers. If two callers race through the gate they each do an
        // enumeration, but both results are valid and the second one simply
        // overwrites the cache entry.
        var fresh = _inner.Enumerate();

        lock (_lock)
        {
            _cached = fresh;
            _cachedAt = DateTime.UtcNow;
        }
        return fresh;
    }
}
