using System;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Peripherals.Hyte.QSeriesCooler;

/// <summary>
/// Singleton coordinator for a HYTE Q-series (Q60 / Q80) cooler controller.
/// Mirrors <see cref="Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubHub"/>:
/// opens the COM port lazily, polls the firmware version, and exposes a state
/// snapshot. Reuses the product-agnostic <see cref="Np50SerialTransport"/>.
///
/// v1 reads firmware version + variant only. The same channel will later carry
/// the in-app "drop into DFU" handshake that the flasher needs.
/// </summary>
public sealed class QSeriesCoolerHub : IDisposable
{
    private readonly IQSeriesCoolerPortDiscovery _discovery;
    private readonly Func<Np50PortInfo, INp50Transport> _transportFactory;
    private readonly object _lock = new();
    private INp50Transport? _transport;
    private bool _disposed;

    public QSeriesCoolerHub(IQSeriesCoolerPortDiscovery discovery, Func<Np50PortInfo, INp50Transport> transportFactory)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
    }

    public QSeriesCoolerState State { get; } = new();
    public bool IsConnected => _transport is { IsOpen: true };

    /// <summary>"q60" / "q80" once connected, else empty. Used as the firmware-catalog key.</summary>
    public string Variant => State.Variant;

    public string DeviceId => string.IsNullOrEmpty(State.Serial) ? "" : $"qseries:{State.Serial}";

    public bool EnsureConnected()
    {
        if (_disposed) return false;
        if (IsConnected) return true;
        lock (_lock)
        {
            if (IsConnected) return true;
            var ports = _discovery.Discover();
            Console.Error.WriteLine($"[qseries-cooler] discovery returned {ports.Count} port(s)");
            foreach (var port in ports)
            {
                try
                {
                    var t = _transportFactory(new Np50PortInfo { PortName = port.PortName, Serial = port.Serial });
                    _transport = t;
                    State.Serial = port.Serial;
                    State.Variant = port.Variant;
                    Console.Error.WriteLine($"[qseries-cooler] connected to {port.PortName} (variant={port.Variant} serial={port.Serial})");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[qseries-cooler] open {port.PortName} failed: {ex.GetType().Name}: {ex.Message}");
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
            transport.Write(QSeriesCoolerProtocol.BuildGetFirmwareVersion());
            var buf = new byte[QSeriesCoolerProtocol.FirmwareVersionResponseLength];
            var n = transport.Read(buf, 400);
            if (n < QSeriesCoolerProtocol.FirmwareVersionResponseLength) { Disconnect(); return false; }
            var v = QSeriesCoolerProtocol.ParseFirmwareVersion(buf.AsSpan(0, n));
            if (!string.IsNullOrEmpty(v)) State.FirmwareVersion = v;
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[qseries-cooler] fw-version exchange failed: {ex.GetType().Name}: {ex.Message}");
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
