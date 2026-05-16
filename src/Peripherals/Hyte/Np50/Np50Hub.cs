using System;
using System.Collections.Generic;
using System.Linq;

namespace Qos.Service.Peripherals.Hyte.Np50;

/// <summary>
/// Singleton coordinator for an NP50 hub. Owns the open transport, the
/// shared <see cref="Np50State"/> snapshot, and the high-level read/write
/// operations the cooling provider, lighting capability, and REST routes
/// call into. Polling is driven by <see cref="Np50HeartbeatWorker"/>; this
/// class itself has no timers.
///
/// Hot-plug is self-healing: each <see cref="EnsureConnected"/> attempt
/// re-runs port discovery, so plugging in or unplugging the hub just shows
/// up on the next heartbeat tick.
/// </summary>
public sealed class Np50Hub : IDisposable
{
    private readonly INp50PortDiscovery _discovery;
    private readonly Func<Np50PortInfo, INp50Transport> _transportFactory;
    private readonly object _lock = new();
    private INp50Transport? _transport;
    private bool _disposed;

    public Np50Hub(INp50PortDiscovery discovery, Func<Np50PortInfo, INp50Transport> transportFactory)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
    }

    /// <summary>Latest state snapshot. Reads are safe without a lock (POCO; eventual consistency is fine for UI).</summary>
    public Np50State State { get; } = new();

    /// <summary>True iff the hub is currently open and reachable.</summary>
    public bool IsConnected => _transport is { IsOpen: true };

    /// <summary>"np50:&lt;serial&gt;" device id, or empty when never connected.</summary>
    public string DeviceId => string.IsNullOrEmpty(State.Serial) ? "" : $"np50:{State.Serial}";

    /// <summary>
    /// Try to open the first NP50 port we can find. No-op if already connected.
    /// Returns true iff a transport is open after the call.
    /// </summary>
    public bool EnsureConnected()
    {
        if (_disposed) return false;
        if (IsConnected) return true;
        lock (_lock)
        {
            if (IsConnected) return true;
            foreach (var port in _discovery.Discover())
            {
                try
                {
                    var t = _transportFactory(port);
                    _transport = t;
                    State.Serial = port.Serial;
                    Console.Error.WriteLine($"[np50] connected to {port.PortName} (serial={port.Serial})");
                    return true;
                }
                catch (Exception ex)
                {
                    // Port enumeration found something but open failed (in use,
                    // permission denied, etc). Move on; next tick tries again.
                    Console.Error.WriteLine($"[np50] open {port.PortName} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            return false;
        }
    }

    /// <summary>Drop the transport (used when a read/write fails so the next tick re-discovers).</summary>
    public void Disconnect()
    {
        lock (_lock)
        {
            try { _transport?.Dispose(); } catch { /* best effort */ }
            _transport = null;
        }
    }

    // ── Polling ──

    /// <summary>Send the heartbeat ("Get NP50 Info") and parse the response into state.</summary>
    public bool PollHubInfo()
    {
        return Exchange(
            Np50Protocol.BuildGetInfo(),
            expectedLength: 20,
            timeoutMs: 250,
            response => Np50Protocol.ParseHubInfo(response, State.HubInfo));
    }

    /// <summary>Poll one port's connected fan list.</summary>
    public bool PollPort(int port)
    {
        // 240 bytes per spec, but the device sometimes sends slightly less when
        // fewer than 19 slots are populated. Read up to the full size and let
        // ParseChannelInfo stop at the first empty slot.
        var p = State.Ports.FirstOrDefault(x => x.Index == port);
        if (p is null) return false;
        return Exchange(
            Np50Protocol.BuildGetChannelInfo(port),
            expectedLength: 240,
            timeoutMs: 400,
            response => Np50Protocol.ParseChannelInfo(response, p));
    }

    public bool PollWarningDetail()
    {
        return Exchange(
            Np50Protocol.BuildGetWarningDetail(),
            expectedLength: 6,
            timeoutMs: 200,
            response => Np50Protocol.ParseWarningDetail(response, State.Warnings));
    }

    public bool PollFirmwareVersion()
    {
        return Exchange(
            Np50Protocol.BuildGetFirmwareVersion(),
            expectedLength: 7,
            timeoutMs: 300,
            response =>
            {
                var v = Np50Protocol.ParseFirmwareVersion(response);
                if (!string.IsNullOrEmpty(v)) State.FirmwareVersion = v;
            });
    }

    // ── Writes ──

    public bool SetCoolingMode(byte mode) => SendOnly(Np50Protocol.BuildSetCoolingMode(mode));

    public bool SetLegacyFanSpeed(int percent)
    {
        // Spec: hub must be in software mode for the legacy 4-pin write to take.
        // Don't re-send mode every call; assume the heartbeat / curve engine
        // owns mode state. Callers that need a mode change call SetCoolingMode first.
        return SendOnly(Np50Protocol.BuildSetLegacyFanSpeed(percent));
    }

    public bool SetPortFanSpeeds(int port, IReadOnlyList<int> perFanPercent)
        => SendOnly(Np50Protocol.BuildSetPortFanSpeeds(port, perFanPercent));

    public bool WriteLighting(int port, ReadOnlySpan<RgbColor> leds)
        => SendOnly(Np50Protocol.BuildLightingStream(port, leds));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }

    // ── Internals ──

    /// <summary>
    /// Run a request/response cycle. Disconnects on IO failure so the next
    /// heartbeat re-discovers; returns false on any error rather than throwing
    /// so the worker loop stays tick-clean.
    /// </summary>
    private bool Exchange(byte[] request, int expectedLength, int timeoutMs, Action<ReadOnlySpan<byte>> parse)
    {
        if (!EnsureConnected()) return false;
        var transport = _transport!;
        try
        {
            transport.Write(request);
            var buf = new byte[expectedLength];
            var n = transport.Read(buf, timeoutMs);
            if (n < 2)
            {
                Console.Error.WriteLine($"[np50] short read ({n} bytes); dropping connection");
                Disconnect();
                return false;
            }
            parse(buf.AsSpan(0, n));
            State.LastPollMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[np50] exchange failed: {ex.GetType().Name}: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    private bool SendOnly(byte[] request)
    {
        if (!EnsureConnected()) return false;
        var transport = _transport!;
        try
        {
            transport.Write(request);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[np50] write failed: {ex.GetType().Name}: {ex.Message}");
            Disconnect();
            return false;
        }
    }
}
