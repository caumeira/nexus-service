using System;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Peripherals.Hyte.Y70Display;

/// <summary>
/// Singleton coordinator for a HYTE Y70 Touch display controller. Mirrors
/// <see cref="Nexus.Service.Peripherals.Hyte.QSeriesCooler.QSeriesCoolerHub"/>:
/// opens the COM port lazily, polls the firmware version, exposes a state
/// snapshot. Reuses the product-agnostic <see cref="Np50SerialTransport"/>.
///
/// v1 reads firmware version + variant only. The same channel will later carry
/// the in-app "drop into DFU" handshake the flasher needs.
/// </summary>
public sealed class Y70DisplayHub : IDisposable
{
    private readonly IY70DisplayPortDiscovery _discovery;
    private readonly Func<Np50PortInfo, INp50Transport> _transportFactory;
    private readonly object _lock = new();
    private INp50Transport? _transport;
    private bool _disposed;

    public Y70DisplayHub(IY70DisplayPortDiscovery discovery, Func<Np50PortInfo, INp50Transport> transportFactory)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
    }

    public Y70DisplayState State { get; } = new();
    public bool IsConnected => _transport is { IsOpen: true };

    /// <summary>"y70" / "y70-infinite" / "y70-truly" once connected, else empty. Firmware-catalog key.</summary>
    public string Variant => State.Variant;

    public string DeviceId => string.IsNullOrEmpty(State.Serial) ? "" : $"y70:{State.Serial}";

    public bool EnsureConnected()
    {
        if (_disposed) return false;
        if (IsConnected) return true;
        lock (_lock)
        {
            if (IsConnected) return true;
            var ports = _discovery.Discover();
            Console.Error.WriteLine($"[y70-display] discovery returned {ports.Count} port(s)");
            foreach (var port in ports)
            {
                try
                {
                    var t = _transportFactory(new Np50PortInfo { PortName = port.PortName, Serial = port.Serial });
                    _transport = t;
                    State.Serial = port.Serial;
                    State.Variant = port.Variant;
                    Console.Error.WriteLine($"[y70-display] connected to {port.PortName} (variant={port.Variant} serial={port.Serial})");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[y70-display] open {port.PortName} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            return false;
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            try { _transport?.Dispose(); } catch { /* best effort */ }
            _transport = null;
        }
    }

    public bool PollFirmwareVersion()
    {
        if (!EnsureConnected()) return false;
        var transport = _transport!;
        try
        {
            transport.DiscardInput();
            transport.Write(Y70DisplayProtocol.BuildGetFirmwareVersion());
            var buf = new byte[Y70DisplayProtocol.FirmwareVersionResponseLength];
            var n = transport.Read(buf, 400);
            if (n < Y70DisplayProtocol.FirmwareVersionResponseLength) { Disconnect(); return false; }
            var v = Y70DisplayProtocol.ParseFirmwareVersion(buf.AsSpan(0, n));
            if (!string.IsNullOrEmpty(v)) State.FirmwareVersion = v;
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[y70-display] fw-version exchange failed: {ex.GetType().Name}: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}
