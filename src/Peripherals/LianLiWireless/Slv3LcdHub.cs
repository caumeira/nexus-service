using System;
using System.Collections.Generic;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Stateless-per-call manager for the SL-LCD Wireless screens: resolves a
/// serial to its device path via <see cref="ISlv3LcdDiscovery"/>, opens and
/// caches one <see cref="ISlv3LcdTransport"/> per serial, and re-opens it if
/// the cached transport has gone stale. Unlike <see cref="Slv3Hub"/> there is
/// no bind/keepalive state machine here; each screen is addressed directly.
/// All shared state is guarded by one lock.
/// </summary>
public sealed class Slv3LcdHub : IDisposable
{
    private readonly ISlv3LcdDiscovery _discovery;
    private readonly Func<Slv3LcdPortInfo, ISlv3LcdTransport> _transportFactory;
    private readonly object _lock = new();
    // Matches FindPort's OrdinalIgnoreCase lookup so the same physical serial
    // in a different casing cannot cache-miss into a second open transport.
    private readonly Dictionary<string, ISlv3LcdTransport> _open = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public Slv3LcdHub(ISlv3LcdDiscovery discovery, Func<Slv3LcdPortInfo, ISlv3LcdTransport> transportFactory)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
    }

    /// <summary>
    /// Currently discovered screens (serial + fan-position mapping), refreshed
    /// each tick by <see cref="Slv3LcdConnectionWorker"/>. Empty until the
    /// worker's first pass.
    /// </summary>
    public IReadOnlyList<Slv3LcdScreenInfo> Screens { get; private set; } = Array.Empty<Slv3LcdScreenInfo>();

    internal void UpdateScreens(IReadOnlyList<Slv3LcdScreenInfo> screens) => Screens = screens;

    /// <summary>Lists the currently enumerable LCD screens (serial + device path).</summary>
    public IReadOnlyList<Slv3LcdPortInfo> Discover() => _discovery.Discover();

    public bool PushImageToSerial(string serial, byte[] jpeg)
    {
        var transport = Resolve(serial);
        if (transport is null)
        {
            return false;
        }
        try
        {
            return transport.PushImage(jpeg);
        }
        catch (ArgumentException ex)
        {
            // A frame decoded from the media library (GIF/video, not size-checked
            // per frame) can exceed the firmware's transfer-buffer capacity that
            // BuildPushJpgBuffer enforces; treat that as a failed push rather
            // than crash the caller.
            ServiceLog.Warn($"[lianli-wireless-lcd] push rejected for {serial}: {ex.Message}");
            return false;
        }
    }

    public bool SetBrightness(string serial, byte value)
    {
        var transport = Resolve(serial);
        return transport is not null && transport.SetBrightness(value);
    }

    public bool SetRotation(string serial, byte value)
    {
        var transport = Resolve(serial);
        return transport is not null && transport.SetRotation(value);
    }

    /// <summary>Queries GetPosIndex(201) for the fan-position GroupIndex. False if the screen is unresolvable or does not ack.</summary>
    public bool TryGetPosition(string serial, out int position)
    {
        position = -1;
        var transport = Resolve(serial);
        if (transport is null || !transport.TryGetPosition(out var group))
        {
            return false;
        }
        position = group;
        return true;
    }

    private ISlv3LcdTransport? Resolve(string serial)
    {
        if (_disposed || string.IsNullOrWhiteSpace(serial))
        {
            return null;
        }
        lock (_lock)
        {
            if (_open.TryGetValue(serial, out var existing))
            {
                if (existing.IsOpen)
                {
                    return existing;
                }
                existing.Dispose();
                _open.Remove(serial);
            }

            var port = FindPort(serial);
            if (port is null)
            {
                return null;
            }

            try
            {
                var transport = _transportFactory(port);
                _open[serial] = transport;
                ServiceLog.Info($"[lianli-wireless-lcd] opened {serial}");
                return transport;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[lianli-wireless-lcd] open failed for {serial}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }

    private Slv3LcdPortInfo? FindPort(string serial)
    {
        foreach (var port in _discovery.Discover())
        {
            if (string.Equals(port.Serial, serial, StringComparison.OrdinalIgnoreCase))
            {
                return port;
            }
        }
        return null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (var transport in _open.Values)
            {
                try
                {
                    transport.Dispose();
                }
                catch
                {
                }
            }
            _open.Clear();
        }
    }
}
