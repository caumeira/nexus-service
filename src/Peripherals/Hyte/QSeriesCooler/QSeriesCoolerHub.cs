using System;
using System.Collections.Generic;
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
    // Last software-commanded pump / fan duty, echoed when toggling turbo or
    // switching to software so the other channel isn't reset.
    private int _lastPumpDuty = 50;
    private int _lastFanDuty = 50;
    // The cooling-policy "pinned" mode (null until the user picks one). The
    // cooling provider reads it to decide whether to swallow engine duty writes
    // so they don't flip the shared pump+fan hub back to software. Reset to null
    // on process start, so a restored profile drives normally. Mirrors the NP50
    // hub's DesiredCoolingMode.
    private byte? _desiredControlMode;

    public QSeriesCoolerHub(IQSeriesCoolerPortDiscovery discovery, Func<Np50PortInfo, INp50Transport> transportFactory)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
    }

    public QSeriesCoolerState State { get; } = new();
    public bool IsConnected => _transport is { IsOpen: true };

    /// <summary>"q60" / "q80" once connected, else empty. Used as the firmware-catalog key.</summary>
    public string Variant => State.Variant;

    /// <summary>User-facing product name for the cooling / lighting pages ("HYTE Q60" / "HYTE Q80").</summary>
    public string ProductName => Variant == QSeriesCoolerProtocol.VariantQ80 ? "HYTE Q80" : "HYTE Q60";

    public string DeviceId => string.IsNullOrEmpty(State.Serial) ? "" : $"qseries:{State.Serial}";

    /// <summary>COM port currently held (e.g. "COM4"), empty when disconnected.</summary>
    public string PortName => _portName;

    /// <summary>True when the connected cooler's firmware supports the editable 5-point temperature curve.</summary>
    public bool SupportsFirmwareCurve =>
        IsConnected && QSeriesCoolerProtocol.SupportsFirmwareCurve(Variant, State.FirmwareVersion);

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

    // Telemetry read deadline. The pump answers a Port-0 query in a few ms; this
    // is the silent-device ceiling. Kept well under the fw-version poll's 400 ms
    // because telemetry polls every heartbeat (3 s) and holds _lock against the
    // 30 Hz lighting writer — a longer deadline would stall the LED stream that
    // long on a marginal serial link.
    private const int TelemetryReadTimeoutMs = 150;

    // The 240-byte Type-M channel-info reply is larger than the pump status, so it
    // gets a longer read deadline. Still well under the fw-version poll's 400 ms.
    private const int FanReadTimeoutMs = 250;

    /// <summary>
    /// Poll pump telemetry (Port-0, plus the Q80 second pump) into <see cref="State"/>.
    /// Read-only on the wire — issues no control writes. Shares <c>_lock</c> with
    /// the 30 Hz lighting stream, so it can't interleave with a frame write. A
    /// short / mis-framed reply skips the update (leaving lighting streaming);
    /// only a thrown transport error tears the port down for the heartbeat to
    /// reconnect.
    /// </summary>
    public bool PollTelemetry()
    {
        lock (_lock)
        {
            if (!EnsureConnected()) return false;
            var transport = _transport;
            if (transport is null) return false;
            try
            {
                transport.DiscardInput();
                transport.Write(QSeriesCoolerProtocol.BuildGetPort0Info());
                var buf = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
                var n = transport.Read(buf, TelemetryReadTimeoutMs);
                if (!QSeriesCoolerProtocol.TryParsePort0PumpRpm(buf.AsSpan(0, n), out var pumpRpm))
                    return false;
                State.PumpRpm = pumpRpm;
                State.ControlMode = QSeriesCoolerProtocol.ControlModeOf(buf);
                State.TurboOn = QSeriesCoolerProtocol.TurboOnOf(buf);

                if (Variant == QSeriesCoolerProtocol.VariantQ80)
                {
                    transport.DiscardInput();
                    transport.Write(QSeriesCoolerProtocol.BuildGetPump2Info());
                    var buf2 = new byte[QSeriesCoolerProtocol.Pump2ResponseLength];
                    var n2 = transport.Read(buf2, TelemetryReadTimeoutMs);
                    if (QSeriesCoolerProtocol.TryParsePump2Rpm(buf2.AsSpan(0, n2), out var pump2))
                    {
                        State.Pump2Rpm = pump2;
                        State.HasPump2 = pump2 > 0;
                    }
                }

                // Radiator fans on the Type-M channel (FF CC 01 02).
                transport.DiscardInput();
                transport.Write(QSeriesCoolerProtocol.BuildGetChannelInfo(QSeriesCoolerProtocol.FanChannel));
                var fbuf = new byte[QSeriesCoolerProtocol.ChannelInfoResponseLength];
                var fn = transport.Read(fbuf, FanReadTimeoutMs);
                if (QSeriesCoolerProtocol.TryParseFanRpm(fbuf.AsSpan(0, fn), out var fanRpm, out var fanPresent))
                {
                    State.HasFan = fanPresent;
                    State.FanRpm = fanRpm;
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] telemetry poll failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    // Caller holds _lock. Reads the 20-byte Port-0 status into buf; false on a
    // short / mis-framed reply. A control write echoes buf's fw-animation bytes.
    private bool ReadPort0(byte[] buf)
    {
        var t = _transport;
        if (t is null) return false;
        t.DiscardInput();
        t.Write(QSeriesCoolerProtocol.BuildGetPort0Info());
        var n = t.Read(buf, TelemetryReadTimeoutMs);
        return n >= QSeriesCoolerProtocol.Port0ResponseLength
            && QSeriesCoolerProtocol.TryParsePort0PumpRpm(buf.AsSpan(0, n), out _);
    }

    /// <summary>
    /// Drive the pump at <paramref name="dutyPercent"/> (0-100) under software
    /// control. Reads Port-0 first to preserve turbo + fw-animation state, maps
    /// the duty to the firmware's voltage byte, then writes the control frame.
    /// </summary>
    public bool SetPumpSpeed(int dutyPercent)
    {
        lock (_lock)
        {
            if (!EnsureConnected()) return false;
            var t = _transport;
            if (t is null) return false;
            try
            {
                var port0 = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
                if (!ReadPort0(port0)) return false;
                var turboOn = QSeriesCoolerProtocol.TurboOnOf(port0);
                var wire = QSeriesCoolerProtocol.MapPumpDutyToWire(dutyPercent, turboOn);
                var turboByte = turboOn ? QSeriesCoolerProtocol.TurboOnByte : QSeriesCoolerProtocol.TurboOffByte;
                // HYTE switches to software mode in one frame, then sends the speed
                // in a SEPARATE frame with the mode byte cleared. Re-asserting the
                // mode in the speed frame resets the pump, so only switch when the
                // hub isn't already in software control.
                if (QSeriesCoolerProtocol.ControlModeOf(port0) != QSeriesCoolerProtocol.ControlModeSoftware)
                    t.Write(QSeriesCoolerProtocol.BuildSetControl(QSeriesCoolerProtocol.ControlModeSoftware, wire, turboByte, port0));
                t.Write(QSeriesCoolerProtocol.BuildSetControl(QSeriesCoolerProtocol.ControlModeKeep, wire, turboByte, port0));
                _lastPumpDuty = Math.Clamp(dutyPercent, 0, 100);
                State.ControlMode = QSeriesCoolerProtocol.ControlModeSoftware;
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] set pump speed failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    /// <summary>
    /// The user-pinned control mode (null until one is chosen, reset on process
    /// start). The cooling provider swallows engine duty writes when this is a
    /// non-software mode so they don't flip the shared pump+fan hub back to
    /// software.
    /// </summary>
    public byte? DesiredControlMode => _desiredControlMode;

    /// <summary>Record the pinned mode without re-issuing a control write (the duty setters assert software live).</summary>
    public void MarkDesiredControlMode(byte? mode)
    {
        lock (_lock) { _desiredControlMode = mode; }
    }

    /// <summary>
    /// Drive the radiator fans at <paramref name="dutyPercent"/> (0-100) under
    /// software control. Ensures the hub is in software mode first (preserving the
    /// pump's last duty), then writes the per-channel fan frame. Plain 0-100% duty.
    /// </summary>
    public bool SetFanSpeed(int dutyPercent)
    {
        lock (_lock)
        {
            if (!EnsureConnected()) return false;
            var t = _transport;
            if (t is null) return false;
            try
            {
                var port0 = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
                if (!ReadPort0(port0)) return false;
                var turboOn = QSeriesCoolerProtocol.TurboOnOf(port0);
                // Off-turbo the firmware ceilings fan duty; apply it host-side so
                // the cooling-card limit line is the actual cap.
                var fanDuty = QSeriesCoolerProtocol.CapFanDutyForTurbo(dutyPercent, turboOn);
                // The fan frame only takes effect in software mode; switch if
                // needed, preserving the pump's last commanded duty so we don't
                // stall it while bringing the fan under control.
                if (QSeriesCoolerProtocol.ControlModeOf(port0) != QSeriesCoolerProtocol.ControlModeSoftware)
                {
                    var pumpWire = QSeriesCoolerProtocol.MapPumpDutyToWire(_lastPumpDuty, turboOn);
                    var turboByte = turboOn ? QSeriesCoolerProtocol.TurboOnByte : QSeriesCoolerProtocol.TurboOffByte;
                    t.Write(QSeriesCoolerProtocol.BuildSetControl(QSeriesCoolerProtocol.ControlModeSoftware, pumpWire, turboByte, port0));
                }
                t.Write(QSeriesCoolerProtocol.BuildSetFanSpeed(QSeriesCoolerProtocol.FanChannel, fanDuty));
                _lastFanDuty = fanDuty;
                State.ControlMode = QSeriesCoolerProtocol.ControlModeSoftware;
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] set fan speed failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    /// <summary>
    /// Switch the hub control mode (Software / Motherboard / Firmware). Reads
    /// Port-0 first to preserve turbo + fw-animation state.
    /// </summary>
    public bool SetControlMode(byte mode)
    {
        lock (_lock)
        {
            if (!EnsureConnected()) return false;
            var t = _transport;
            if (t is null) return false;
            try
            {
                var port0 = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
                if (!ReadPort0(port0)) return false;
                t.Write(QSeriesCoolerProtocol.BuildSetControl(
                    mode, 0,
                    QSeriesCoolerProtocol.TurboOnOf(port0) ? QSeriesCoolerProtocol.TurboOnByte : QSeriesCoolerProtocol.TurboOffByte,
                    port0));
                State.ControlMode = mode;
                _desiredControlMode = mode; // pin: a user-chosen mode the engine must not override
                // The live control byte alone doesn't engage the onboard curve;
                // firmware mode also needs the EEPROM default flipped to Temperature
                // (and reset to Motherboard when handing back), preserving the
                // stored curve. Mirrors HYTE SwitchToTemperatureMode /
                // SetFirmwareToMotherboardMode. These extra read + write round-trips
                // run under _lock, so this rare user-initiated switch can delay the
                // 30 Hz lighting stream more than a plain control write. Best-effort:
                // the live mode already changed, so a curve read/write hiccup here
                // must not fail the whole switch (the next poll re-reads true mode).
                if (SupportsFirmwareCurve)
                {
                    try
                    {
                        if (mode == QSeriesCoolerProtocol.ControlModeFirmware)
                            SetFirmwareDefaultModeLocked(t, QSeriesCoolerProtocol.FwDefaultModeTemperature);
                        else if (mode == QSeriesCoolerProtocol.ControlModeMotherboard)
                            SetFirmwareDefaultModeLocked(t, QSeriesCoolerProtocol.FwDefaultModeMotherboard);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[qseries-cooler] firmware default-mode engage failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] set control mode failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    /// <summary>
    /// Toggle turbo. Writes the control frame (preserving the current mode and
    /// last-commanded pump duty) then persists the turbo flag to the MCU (FF CC 0A).
    /// </summary>
    public bool SetTurbo(bool on)
    {
        lock (_lock)
        {
            if (!EnsureConnected()) return false;
            var t = _transport;
            if (t is null) return false;
            try
            {
                var port0 = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
                if (!ReadPort0(port0)) return false;
                var mode = QSeriesCoolerProtocol.ControlModeOf(port0);
                var wire = QSeriesCoolerProtocol.MapPumpDutyToWire(_lastPumpDuty, on);
                var turboByte = on ? QSeriesCoolerProtocol.TurboOnByte : QSeriesCoolerProtocol.TurboOffByte;
                t.Write(QSeriesCoolerProtocol.BuildSetControl(mode, wire, turboByte, port0));
                t.Write(QSeriesCoolerProtocol.BuildSetTurboMcu(turboByte));
                State.TurboOn = on;
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] set turbo failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    // EEPROM curve read deadline. The default-mode response comes back in a few
    // ms; kept under the fw-version poll's 400 ms but above the 150 ms telemetry
    // ceiling since this is a user-initiated read, not the hot lighting path.
    private const int FirmwareReadTimeoutMs = 300;

    /// <summary>
    /// Read the stored 5-point firmware temperature curve (FF CC 04) into
    /// <paramref name="points"/>. False on disconnect, an unsupported firmware,
    /// or a short / mis-framed reply.
    /// </summary>
    public bool TryReadFirmwareCurve(out QSeriesFirmwareCurvePoint[] points)
    {
        points = Array.Empty<QSeriesFirmwareCurvePoint>();
        lock (_lock)
        {
            if (!EnsureConnected() || !SupportsFirmwareCurve) return false;
            var t = _transport;
            if (t is null) return false;
            try
            {
                return TryReadFirmwareCurveLocked(t, out points, out _);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] read firmware curve failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    /// <summary>
    /// Persist a new 5-point firmware temperature curve to EEPROM (FF CC 03),
    /// preserving the current default mode so saving the curve never changes which
    /// controller drives the pump. False on disconnect / unsupported firmware.
    /// </summary>
    public bool WriteFirmwareCurve(IReadOnlyList<QSeriesFirmwareCurvePoint> points)
    {
        if (points.Count != QSeriesCoolerProtocol.FirmwareCurvePointCount) return false;
        lock (_lock)
        {
            if (!EnsureConnected() || !SupportsFirmwareCurve) return false;
            var t = _transport;
            if (t is null) return false;
            try
            {
                // Read back the current default mode so the write preserves it.
                var mode = TryReadFirmwareCurveLocked(t, out _, out var current)
                    ? current
                    : QSeriesCoolerProtocol.FwDefaultModeMotherboard;
                var arr = new QSeriesFirmwareCurvePoint[points.Count];
                for (var i = 0; i < points.Count; i++) arr[i] = points[i];
                t.Write(QSeriesCoolerProtocol.BuildSetFirmwareMode(mode, arr));
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qseries-cooler] write firmware curve failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    // Caller holds _lock. Reads FF CC 04 into a parsed curve + the current default
    // mode byte. False on a short / mis-framed reply.
    private bool TryReadFirmwareCurveLocked(INp50Transport t, out QSeriesFirmwareCurvePoint[] points, out byte defaultMode)
    {
        points = Array.Empty<QSeriesFirmwareCurvePoint>();
        defaultMode = QSeriesCoolerProtocol.FwDefaultModeMotherboard;
        t.DiscardInput();
        t.Write(QSeriesCoolerProtocol.BuildGetFirmwareDefault());
        var buf = new byte[QSeriesCoolerProtocol.FirmwareDefaultResponseLength];
        var n = t.Read(buf, FirmwareReadTimeoutMs);
        if (!QSeriesCoolerProtocol.TryParseFirmwareCurve(buf.AsSpan(0, n), out points)) return false;
        defaultMode = QSeriesCoolerProtocol.FirmwareDefaultModeOf(buf.AsSpan(0, n));
        return true;
    }

    // Caller holds _lock. Flips the EEPROM default mode to newMode while preserving
    // the stored curve (read it back, re-send with the new mode). No-op when already
    // in newMode or when the read fails.
    private void SetFirmwareDefaultModeLocked(INp50Transport t, byte newMode)
    {
        if (!TryReadFirmwareCurveLocked(t, out var curve, out var current)) return;
        if (current == newMode || curve.Length != QSeriesCoolerProtocol.FirmwareCurvePointCount) return;
        t.Write(QSeriesCoolerProtocol.BuildSetFirmwareMode(newMode, curve));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}
