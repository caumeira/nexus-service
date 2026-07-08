using System;
using System.Collections.Generic;

namespace Nexus.Service.Devices.Detection;

/// <summary>
/// Single-flight, short-TTL cache over any <see cref="IUsbEnumerator"/>.
/// Roughly a dozen periodic callers (device heartbeat workers, the Devices
/// tab polls, PeripheralRegistry) funnel through this cache; without the
/// refresh lock every caller that arrived during a refresh started its own
/// bus scan, so each TTL expiry spawned several concurrent scans.
///
/// The TTL is the hotplug-detection latency bound. It starts at 10 s and is
/// raised by <see cref="UsbDeviceChangeNotifier"/> once PnP device-change
/// notifications are registered - the TTL then only backstops a missed
/// notification (e.g. across a sleep/resume re-enumeration storm).
/// </summary>
internal sealed class CachingUsbEnumerator : IUsbEnumerator
{
    private static readonly TimeSpan PollingTtl = TimeSpan.FromSeconds(10);

    private readonly IUsbEnumerator _inner;
    private readonly object _stateLock = new();
    private readonly object _refreshLock = new();
    private List<UsbDeviceEntry>? _cached;
    private DateTime _cachedAt;
    private int _version;
    private TimeSpan _ttl = PollingTtl;

    public CachingUsbEnumerator(IUsbEnumerator inner) { _inner = inner; }

    /// <summary>Drops the cached list so the next Enumerate re-scans the bus.</summary>
    public void Invalidate()
    {
        lock (_stateLock)
        {
            _version++;
            _cached = null;
        }
    }

    /// <summary>
    /// Fallback TTL while event-driven invalidation is active. Must stay short
    /// enough that a missed PnP notification only delays hotplug detection,
    /// never masks it: heartbeat workers skip connection attempts entirely
    /// while the cache says their device is absent.
    /// </summary>
    public void SetTtl(TimeSpan ttl)
    {
        lock (_stateLock)
        {
            _ttl = ttl;
        }
    }

    public List<UsbDeviceEntry> Enumerate()
    {
        if (TryGetFresh(out var cached))
        {
            return cached;
        }

        // Single-flight: the first expired-cache caller scans; the rest block
        // here and serve the refreshed cache instead of scanning themselves.
        lock (_refreshLock)
        {
            if (TryGetFresh(out cached))
            {
                return cached;
            }

            int versionAtStart;
            lock (_stateLock)
            {
                versionAtStart = _version;
            }

            var fresh = _inner.Enumerate();

            lock (_stateLock)
            {
                // An Invalidate during the scan means the bus changed mid-scan:
                // serve this result but leave the cache stale so the next
                // caller re-scans.
                if (_version == versionAtStart)
                {
                    _cached = fresh;
                    _cachedAt = DateTime.UtcNow;
                }
            }
            return fresh;
        }
    }

    private bool TryGetFresh(out List<UsbDeviceEntry> cached)
    {
        lock (_stateLock)
        {
            if (_cached is not null && DateTime.UtcNow - _cachedAt < _ttl)
            {
                cached = _cached;
                return true;
            }
        }
        cached = null!;
        return false;
    }
}
