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
    private readonly Dictionary<string, ISlv3LcdTransport> _open = new(StringComparer.Ordinal);
    private bool _disposed;

    public Slv3LcdHub(ISlv3LcdDiscovery discovery, Func<Slv3LcdPortInfo, ISlv3LcdTransport> transportFactory)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
    }

    /// <summary>Lists the currently enumerable LCD screens (serial + device path).</summary>
    public IReadOnlyList<Slv3LcdPortInfo> Discover() => _discovery.Discover();

    public bool PushImageToSerial(string serial, byte[] jpeg)
    {
        var transport = ResolveLocked(serial);
        return transport is not null && transport.PushImage(jpeg);
    }

    public bool SetBrightness(string serial, byte value)
    {
        var transport = ResolveLocked(serial);
        return transport is not null && transport.SetBrightness(value);
    }

    public bool SetRotation(string serial, byte value)
    {
        var transport = ResolveLocked(serial);
        return transport is not null && transport.SetRotation(value);
    }

    private ISlv3LcdTransport? ResolveLocked(string serial)
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
