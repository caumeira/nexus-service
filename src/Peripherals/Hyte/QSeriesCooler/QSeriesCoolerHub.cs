using System;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Platform;
// Q-series shares the MiniHub RGB triple; alias to avoid the Np50.RgbColor clash.
using RgbColor = Nexus.Service.Peripherals.Hyte.MiniHub.RgbColor;

namespace Nexus.Service.Peripherals.Hyte.QSeriesCooler;

/// <summary>
/// Singleton coordinator for a HYTE Q-series (Q60 / Q80) cooler controller.
/// Mirrors <see cref="Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubHub"/>:
/// opens the COM port lazily, polls the firmware version, exposes a state
/// snapshot, streams LED frames, and carries the "drop into DFU" handshake.
/// Reuses the product-agnostic <see cref="Np50SerialTransport"/>.
/// </summary>
public sealed class QSeriesCoolerHub : IDisposable, IDfuFlashTarget
{
    private readonly IQSeriesCoolerPortDiscovery _discovery;
    private readonly Func<Np50PortInfo, INp50Transport> _transportFactory;
    private readonly object _lock = new();
    private INp50Transport? _transport;
    private bool _disposed;
    // Set once we've put the cooler into software RGB control; cleared on
    // disconnect so the next connection re-asserts it before streaming.
    private bool _rgbInSwControl;
    // The COM port currently held (empty when disconnected). Lets the composite
    // strip OpenRGB's zombie entry for the same port without fragile name matching.
    private string _portName = "";
    // Starts at 0 (the silent default) so a device absent from boot never logs
    // "discovery returned 0"; only a real change (0->N found, or N->0 disconnect) logs.
    private int _lastDiscoveredPortCount;

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

    /// <summary>COM port currently held (e.g. "COM4"), empty when disconnected.</summary>
    public string PortName => _portName;

    /// <summary>
    /// LEDs addressed per Q-series lighting card. One 90-byte port frame carries
    /// <see cref="QSeriesCoolerProtocol.MaxLedsPerPort"/> (27) LEDs; surfaces a single
    /// linear zone of that size streamed to every port (see <see cref="WriteLighting"/>).
    /// </summary>
    public const int LedCount = QSeriesCoolerProtocol.MaxLedsPerPort;

    /// <summary>
    /// Q-series needs no settings-first handshake (unlike CNVS), so streaming is gated only on
    /// the port being open. Software RGB control is asserted lazily in <see cref="WriteLighting"/>
    /// on the first frame after each (re)connect.
    /// </summary>
    public bool IsReadyForStreaming => IsConnected;

    /// <summary>
    /// Stream one frame of LED colors to the cooler. Mirrors the legacy
    /// PQSeriesDeviceBase.SendToHardware loop: assert software RGB control once per connection,
    /// then write all <see cref="QSeriesCoolerProtocol.LedPortCount"/> port frames. The same colors
    /// go to every port so the pump-head channel lights regardless of which physical port it
    /// occupies; ports with no LEDs ignore the data. Colors are RGB here;
    /// <see cref="QSeriesCoolerProtocol.BuildLightingStream"/> emits GRB on the wire.
    /// </summary>
    public void WriteLighting(ReadOnlySpan<RgbColor> leds)
    {
        // Serialize the write sequence under _lock (re-entrant): the 30 Hz
        // frame writer and the 3 s heartbeat both touch the transport, and Disconnect
        // disposes it under the same lock. Without this a heartbeat-triggered Disconnect
        // could tear the port down mid-frame, and _rgbInSwControl could be read stale
        // across a reconnect. Mirrors CnvsHub's _writeLock discipline.
        lock (_lock)
        {
            if (!EnsureConnected()) return;
            var t = _transport;
            if (t is null) return;
            try
            {
                if (!_rgbInSwControl)
                {
                    t.Write(QSeriesCoolerProtocol.BuildSetRgbControlMode(QSeriesCoolerProtocol.RgbModeSoftware));
                    _rgbInSwControl = true;
                }
                for (var port = 1; port <= QSeriesCoolerProtocol.LedPortCount; port++)
                    t.Write(QSeriesCoolerProtocol.BuildLightingStream(port, leds));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] lighting write failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
            }
        }
    }

    // ── IDfuFlashTarget ──
    string IDfuFlashTarget.FirmwareType => IsConnected ? Variant : "";
    bool IDfuFlashTarget.CanFlash(string firmwareType) =>
        firmwareType == QSeriesCoolerProtocol.VariantQ60 || firmwareType == QSeriesCoolerProtocol.VariantQ80;

    /// <summary>Drop the Q-series cooler into DFU: write the OTA key + magic over the serial port, then release it.</summary>
    public bool EnterDfuMode()
    {
        if (!EnsureConnected()) return false;
        var t = _transport;
        var pid = QSeriesCoolerProtocol.ProductIdForVariant(Variant);
        if (t is null || pid < 0) return false;
        var verify = OtaDfuEntry.SupportsPidCheck(Variant, State.FirmwareVersion);
        bool ok;
        try { ok = OtaDfuEntry.Enter(t, OtaProductKey.ForProductId(pid), verify); }
        catch { ok = true; }
        Disconnect();
        return ok;
    }

    public bool EnsureConnected()
    {
        if (_disposed) return false;
        if (IsConnected) return true;
        lock (_lock)
        {
            if (IsConnected) return true;
            var ports = _discovery.Discover();
            if (ports.Count != _lastDiscoveredPortCount)
            {
                _lastDiscoveredPortCount = ports.Count;
                ServiceLog.Info($"[qseries-cooler] discovery returned {ports.Count} port(s)");
            }
            foreach (var port in ports)
            {
                try
                {
                    var t = _transportFactory(new Np50PortInfo { PortName = port.PortName, Serial = port.Serial });
                    _transport = t;
                    State.Serial = port.Serial;
                    State.Variant = port.Variant;
                    _portName = port.PortName;
                    ServiceLog.Info($"[qseries-cooler] connected to {port.PortName} (variant={port.Variant} serial={port.Serial})");
                    return true;
                }
                catch (Exception ex)
                {
                    ServiceLog.Error($"[qseries-cooler] open {port.PortName} failed: {ex.GetType().Name}: {ex.Message}");
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
            _rgbInSwControl = false;
            _portName = "";
        }
    }

    public bool PollFirmwareVersion()
    {
        // Under _lock so the fw-version request/response can't interleave with the
        // 30 Hz lighting stream now that WriteLighting runs on a separate thread.
        lock (_lock)
        {
            if (!EnsureConnected()) return false;
            var transport = _transport;
            if (transport is null) return false;
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}
