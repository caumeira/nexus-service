using System;
using Qos.Service.Peripherals.Hyte.Np50;

namespace Qos.Service.Peripherals.Hyte.MiniHub;

/// <summary>
/// Singleton coordinator for a HYTE IBP MiniHub. Mirrors <see cref="Np50Hub"/>
/// for the MiniHub product: opens the COM port lazily, exposes a state
/// snapshot, and lets capability classes push LED frames. v1 ships
/// lighting-only — fan poll/control is a follow-up.
/// </summary>
public sealed class MiniHubHub : IDisposable
{
    private readonly INp50PortDiscovery _discovery;
    private readonly Func<Np50PortInfo, INp50Transport> _transportFactory;
    private readonly object _lock = new();
    private INp50Transport? _transport;
    private bool _disposed;

    public MiniHubHub(INp50PortDiscovery discovery, Func<Np50PortInfo, INp50Transport> transportFactory)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
    }

    public MiniHubState State { get; } = new();
    public bool IsConnected => _transport is { IsOpen: true };
    public string DeviceId => string.IsNullOrEmpty(State.Serial) ? "" : $"minihub:{State.Serial}";

    public bool EnsureConnected()
    {
        if (_disposed) return false;
        if (IsConnected) return true;
        lock (_lock)
        {
            if (IsConnected) return true;
            var ports = _discovery.Discover();
            // One-line trace so we can tell "no device" apart from "device
            // found but port open failed" — both manifest as the hub
            // silently staying disconnected. Logged at most every heartbeat
            // tick which is fine.
            Console.Error.WriteLine($"[minihub] discovery returned {ports.Count} port(s)");
            foreach (var port in ports)
            {
                try
                {
                    var t = _transportFactory(port);
                    _transport = t;
                    State.Serial = port.Serial;
                    Console.Error.WriteLine($"[minihub] connected to {port.PortName} (serial={port.Serial})");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[minihub] open {port.PortName} failed: {ex.GetType().Name}: {ex.Message}");
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
            transport.Write(MiniHubProtocol.BuildGetFirmwareVersion());
            var buf = new byte[7];
            var n = transport.Read(buf, 300);
            if (n < 7) { Disconnect(); return false; }
            var v = MiniHubProtocol.ParseFirmwareVersion(buf.AsSpan(0, n));
            if (!string.IsNullOrEmpty(v)) State.FirmwareVersion = v;
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[minihub] fw-version exchange failed: {ex.GetType().Name}: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    public bool SetRgbControlMode(byte mode) => SendOnly(MiniHubProtocol.BuildSetRgbControlMode(mode));

    public bool WriteLighting(int channel, ReadOnlySpan<RgbColor> leds)
        => SendOnly(MiniHubProtocol.BuildLightingStream(channel, leds));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }

    private bool SendOnly(byte[] request)
    {
        if (!EnsureConnected()) return false;
        var transport = _transport!;
        try { transport.Write(request); return true; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[minihub] write failed: {ex.GetType().Name}: {ex.Message}");
            Disconnect();
            return false;
        }
    }
}
