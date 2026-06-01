using System;
using System.Threading;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Peripherals.Hyte.SmartHub;

/// <summary>
/// Singleton coordinator for a HYTE Smart Hub. Mirrors
/// <see cref="Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubHub"/>: opens the
/// COM port lazily, exposes a <see cref="SmartHubState"/> snapshot, and lets
/// the lighting + cooling capability classes push LED frames / fan speeds.
///
/// It reuses the product-agnostic NP50 serial transport + port-discovery
/// abstraction (<see cref="INp50Transport"/> / <see cref="INp50PortDiscovery"/>)
/// — the Smart Hub is just another HYTE serial-over-USB hub, so there's no
/// reason to duplicate the serial plumbing.
/// </summary>
public sealed class SmartHubHub : IDisposable
{
    /// <summary>
    /// Single source of truth for this device's user-facing product label.
    /// Both the lighting and cooling providers reference this so the panel
    /// shows the SAME name everywhere. Matches HYTE's own product taxonomy
    /// for VID_3402&amp;PID_0904 (<c>UniversalHardwareInfo</c> = "HYTE Smart Hub").
    /// </summary>
    public const string ProductName = "HYTE Smart Hub";

    private readonly INp50PortDiscovery _discovery;
    private readonly Func<Np50PortInfo, INp50Transport> _transportFactory;
    private readonly object _lock = new();
    private INp50Transport? _transport;
    private bool _disposed;

    private int _consecutiveWriteFailures;
    private const int ConsecutiveWriteFailureThreshold = 5;

    public SmartHubHub(INp50PortDiscovery discovery, Func<Np50PortInfo, INp50Transport> transportFactory)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
    }

    public SmartHubState State { get; } = new();
    public bool IsConnected => _transport is { IsOpen: true };
    public string DeviceId => string.IsNullOrEmpty(State.Serial) ? "" : $"smarthub:{State.Serial}";

    public bool EnsureConnected()
    {
        if (_disposed) return false;
        if (IsConnected) return true;
        lock (_lock)
        {
            if (IsConnected) return true;
            var ports = _discovery.Discover();
            Console.Error.WriteLine($"[smarthub] discovery returned {ports.Count} port(s)");
            foreach (var port in ports)
            {
                try
                {
                    _transport = _transportFactory(port);
                    State.Serial = port.Serial;
                    Console.Error.WriteLine($"[smarthub] connected to {port.PortName} (serial={port.Serial})");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[smarthub] open {port.PortName} failed: {ex.GetType().Name}: {ex.Message}");
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

    /// <summary>Read the firmware version once on connect. Returns false on transport hiccup so the heartbeat can re-discover.</summary>
    public bool PollFirmwareVersion()
    {
        if (!EnsureConnected()) return false;
        var transport = _transport!;
        try
        {
            transport.DiscardInput();
            transport.Write(SmartHubProtocol.BuildGetFirmwareVersion());
            var buf = new byte[SmartHubProtocol.FirmwareVersionResponseLength];
            var n = transport.Read(buf, 300);
            if (n < SmartHubProtocol.FirmwareVersionResponseLength) { Disconnect(); return false; }
            var v = SmartHubProtocol.ParseFirmwareVersion(buf.AsSpan(0, n));
            if (!string.IsNullOrEmpty(v)) State.FirmwareVersion = v;
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[smarthub] fw-version exchange failed: {ex.GetType().Name}: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    /// <summary>
    /// Poll the 20-byte hub-info response and stash per-channel tach + enabled
    /// state on <see cref="State"/>. Returns false on transport hiccup or a
    /// malformed reply so the heartbeat can drop the transport and re-discover.
    /// </summary>
    public bool PollChannelInfo()
    {
        if (!EnsureConnected()) return false;
        var transport = _transport!;
        try
        {
            transport.DiscardInput();
            transport.Write(SmartHubProtocol.BuildGetInfo());
            var buf = new byte[SmartHubProtocol.GetInfoResponseLength];
            var n = transport.Read(buf, 300);
            if (n < SmartHubProtocol.GetInfoResponseLength) { Disconnect(); return false; }
            if (!SmartHubProtocol.TryParseChannelInfo(buf.AsSpan(0, n), out var channels) || channels is null)
                return false;
            for (var i = 0; i < State.Fans.Length && i < channels.Length; i++)
            {
                State.Fans[i].Rpm = channels[i].Rpm;
                State.Fans[i].Enabled = channels[i].Enabled;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[smarthub] get-info exchange failed: {ex.GetType().Name}: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    /// <summary>Turn the hub's onboard LED animation on/off. Off ⇒ software streaming drives the ARGB ports.</summary>
    public bool SetFirmwareAnimation(bool on) => SendOnly(SmartHubProtocol.BuildSetFirmwareAnimation(on));

    /// <summary>Stream a rendered LED frame to one ARGB port (1..4).</summary>
    public bool WriteLighting(int port, ReadOnlySpan<RgbColor> leds)
        => SendOnly(SmartHubProtocol.BuildLightingStream(port, leds));

    /// <summary>
    /// Set one PWM-fan port's duty (0..100%). Records the commanded value on
    /// <see cref="State"/> so the cooling provider can read it back without a
    /// round-trip. <paramref name="enabled"/> gates the port output.
    /// </summary>
    public bool WriteFanSpeed(int channel, int dutyPercent, bool enabled = true)
    {
        if (channel < 0 || channel >= State.Fans.Length) return false;
        var ok = SendOnly(SmartHubProtocol.BuildSetFanSpeed(channel, dutyPercent, enabled));
        if (ok)
        {
            State.Fans[channel].Duty = Math.Clamp(dutyPercent, SmartHubProtocol.FanMinDutyPercent, SmartHubProtocol.FanMaxDutyPercent);
            State.Fans[channel].Enabled = enabled;
        }
        return ok;
    }

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
        try
        {
            transport.Write(request);
            _consecutiveWriteFailures = 0;
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            // Same rationale as MiniHubHub.SendOnly / Np50Hub.SendOnly: a
            // single transient write hiccup at 30 Hz shouldn't tear down the
            // shared transport (which could surface as visible flicker).
            // Soften to "log and retry" until a sustained burst makes it clear
            // the port is actually dead.
            var n = Interlocked.Increment(ref _consecutiveWriteFailures);
            Console.Error.WriteLine($"[smarthub] write failed (#{n}): {ex.GetType().Name}: {ex.Message}");
            if (n >= ConsecutiveWriteFailureThreshold)
            {
                Console.Error.WriteLine($"[smarthub] {n} consecutive write failures — dropping transport so next tick rediscovers");
                _consecutiveWriteFailures = 0;
                Disconnect();
            }
            return false;
        }
    }
}
