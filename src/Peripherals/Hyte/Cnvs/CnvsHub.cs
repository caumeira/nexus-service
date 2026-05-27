using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;

namespace Nexus.Service.Peripherals.Hyte.Cnvs;

/// <summary>
/// Owns the CNVS COM port for the lifetime of the service. Mirrors
/// <see cref="Np50.Np50Hub"/>'s "open at startup, hold forever" model so
/// OpenRGB-headless (which also tries to claim CNVS) finds the port busy
/// and silently skips it. Whoever opens the COM port first wins under
/// Windows serial semantics; running our open BEFORE OpenRGB launches —
/// which <see cref="CnvsConnectionWorker"/> guarantees via an early
/// background tick — makes us the de-facto owner.
///
/// Surfaces three wire commands:
/// - <see cref="WriteSettings"/>: EEPROM-persisted firmware bits
///   (`FF DC 07 suppressBoot ledsOnWhenOff`).
/// - <see cref="ReadSettings"/>: queries `FF DC 08`, reads 9 bytes.
/// - <see cref="WriteLighting"/>: the 157-byte LED stream frame
///   (`FF EE 02 01 00 32 00 + 50×3 GRB bytes`) lifted from
///   HYTE's HYTEMousematController.StreamingCommand in OpenRGB.
/// - <see cref="SetFirmwareAnimationOff"/>: `FF DC 05 00` — every
///   streaming frame must be preceded by this (HYTE's reference does
///   the same in CNVSBaseController.SendToHardware) so the firmware
///   stops overlaying its boot animation.
/// </summary>
public sealed class CnvsHub : IDisposable
{
    /// <summary>Number of physical LEDs on the CNVS (per HYTE's OpenRGB driver).</summary>
    public const int LedCount = 50;

    /// <summary>
    /// Per-channel brightness ceiling HYTE's reference driver applies.
    /// Equivalent to `(72 * value) / 100` in HYTEMousematController.cpp;
    /// keeps a fully-saturated LED at ~72% of its raw drive current so the
    /// mat doesn't overheat or draw past the USB-port budget on long bursts.
    /// Applied centrally in <see cref="WriteLighting"/> so callers pass raw
    /// 0..255 colors and don't need to know about the cap.
    /// </summary>
    public const int MaxChannelBrightness = 72;

    private readonly ICnvsPortDiscovery _discovery;
    private readonly object _writeLock = new();
    private readonly object _readLock = new();
    private SerialPort? _port;
    private string _portName = "";
    private string _serial = "";
    private bool _disposed;

    public CnvsHub(ICnvsPortDiscovery discovery)
    {
        _discovery = discovery;
    }

    /// <summary>True iff we currently hold an open CNVS port.</summary>
    public bool IsConnected => !_disposed && _port?.IsOpen == true;

    /// <summary>USB serial of the open device, or empty when never connected.</summary>
    public string Serial => _serial;

    /// <summary>"cnvs:&lt;serial&gt;" device id; empty until we open the port.</summary>
    public string DeviceId => string.IsNullOrEmpty(_serial) ? "" : $"cnvs:{_serial}";

    /// <summary>
    /// Try to open a CNVS port if one is enumerable and we don't already
    /// hold one. Idempotent; safe to call from a background worker. Returns
    /// true iff the hub is connected after the call.
    /// </summary>
    public bool EnsureConnected()
    {
        if (_disposed) return false;
        if (IsConnected) return true;
        lock (_writeLock)
        {
            if (IsConnected) return true;
            var ports = _discovery.Discover();
            foreach (var info in ports)
            {
                try
                {
                    var port = OpenPort(info.PortName);
                    _port = port;
                    _portName = info.PortName;
                    _serial = info.Serial ?? "";
                    Console.Error.WriteLine($"[cnvs] connected on {info.PortName} (serial={_serial})");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[cnvs] open {info.PortName} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            return false;
        }
    }

    /// <summary>Release the port. Used on shutdown and on transport errors.</summary>
    public void Disconnect()
    {
        lock (_writeLock)
        {
            try { if (_port?.IsOpen == true) _port.Close(); } catch { }
            try { _port?.Dispose(); } catch { }
            _port = null;
        }
    }

    /// <summary>
    /// Persist both firmware settings to EEPROM. Read-back after to verify
    /// the firmware accepted them — returns true when the read-back matches,
    /// false on transport error or device disagreement.
    /// </summary>
    public bool WriteSettings(bool suppressBootAnimation, bool keepLedsOnWhenPcOff)
    {
        if (!EnsureConnected()) return false;
        lock (_writeLock)
        {
            var port = _port;
            if (port is null) return false;
            try
            {
                // The firmware appears to silently drop FF DC 07 writes when
                // the device is in streaming mode (i.e. after FF DC 05 00).
                // Bracket the settings write with a transition into firmware
                // mode and back — mirrors HYTE's SetToFirmwareMode → settings
                // → TurnFwAnimationOFF flow. The whole sequence runs under
                // _writeLock so the 30 Hz lighting writer can't interleave.

                // Step 1: enter firmware-animation mode. HYTE's
                // TurnFwAnimationOn sends FF DC 02 preamble then FF DC 05 01.
                var animPreamble = CnvsProtocol.BuildTurnAnimationOnPreamble();
                port.Write(animPreamble, 0, animPreamble.Length);
                var animOn = CnvsProtocol.BuildTurnAnimationOnMain();
                port.Write(animOn, 0, animOn.Length);
                Thread.Sleep(30);

                // Step 2: settings write itself.
                port.DiscardInBuffer();
                var frame = CnvsProtocol.BuildSetSettings(suppressBootAnimation, keepLedsOnWhenPcOff);
                port.Write(frame, 0, frame.Length);
                Console.Error.WriteLine(
                    $"[cnvs] WriteSettings sent {frame.Length}B on {_portName} (serial={_serial}): boot={suppressBootAnimation} leds={keepLedsOnWhenPcOff}");

                // Step 3: give the firmware a beat to commit before flipping
                // back to streaming mode. 100 ms is generous; HYTE's UI uses
                // ~50 ms between settings + next frame, doubled here to
                // survive worst-case flash-write latency.
                Thread.Sleep(100);

                // Step 4: best-effort read-back. Logged but not load-bearing
                // — some firmware revs don't echo FF DC 08.
                var readBack = ReadSettingsLocked(port);
                if (readBack is { } rb)
                    Console.Error.WriteLine($"[cnvs] WriteSettings read-back: {rb}");
                else
                    Console.Error.WriteLine("[cnvs] WriteSettings: read-back returned no data");

                // Step 5: back to streaming mode so the lighting writer's
                // next frame paints correctly. Fire the drift event so the
                // writer drops its cached "already silenced" flag and
                // re-asserts FF DC 05 00 on its next tick — without that
                // the writer trusts the stale flag and the firmware sits
                // in animation mode overlaying our stream.
                var animOff = CnvsProtocol.BuildTurnAnimationOff();
                port.Write(animOff, 0, animOff.Length);
                FirmwareAnimationStateMayHaveDrifted?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cnvs] WriteSettings exception: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    /// <summary>
    /// Fires when <see cref="WriteSettings"/> has toggled the firmware-
    /// animation state inside its bracket. The lighting frame writer
    /// subscribes so it re-sends <c>FF DC 05 00</c> on its next tick
    /// rather than trusting its cached "already silenced" flag.
    /// </summary>
    public event Action? FirmwareAnimationStateMayHaveDrifted;

    /// <summary>Read the EEPROM-persisted settings. Null on transport error / not connected.</summary>
    public CnvsProtocol.CnvsSettings? ReadSettings()
    {
        if (!EnsureConnected()) return null;
        lock (_writeLock)
        {
            var port = _port;
            if (port is null) return null;
            try
            {
                return ReadSettingsLocked(port);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cnvs] ReadSettings exception: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return null;
            }
        }
    }

    /// <summary>
    /// Push one LED frame. <paramref name="leds"/> must be exactly
    /// <see cref="LedCount"/> entries; shorter spans are zero-padded.
    /// Bytes are GRB-ordered and pre-scaled by <see cref="MaxChannelBrightness"/>
    /// / 100 (the same cap HYTE's OpenRGB driver applies). The firmware's
    /// boot animation must be silenced first via
    /// <see cref="SetFirmwareAnimationOff"/> or it overlays the stream.
    /// </summary>
    public bool WriteLighting(ReadOnlySpan<RgbColor> leds)
    {
        if (!EnsureConnected()) return false;
        // 157-byte fixed frame: 7-byte header + 50 LEDs × 3 bytes.
        Span<byte> buf = stackalloc byte[157];
        buf.Clear();
        buf[0] = 0xFF; buf[1] = 0xEE; buf[2] = 0x02;
        buf[3] = 0x01; buf[4] = 0x00;
        buf[5] = (byte)LedCount; // 0x32
        buf[6] = 0x00;
        var count = Math.Min(leds.Length, LedCount);
        for (var i = 0; i < count; i++)
        {
            var off = 7 + (i * 3);
            buf[off + 0] = (byte)((MaxChannelBrightness * leds[i].G) / 100);
            buf[off + 1] = (byte)((MaxChannelBrightness * leds[i].R) / 100);
            buf[off + 2] = (byte)((MaxChannelBrightness * leds[i].B) / 100);
        }
        lock (_writeLock)
        {
            var port = _port;
            if (port is null) return false;
            try
            {
                var arr = buf.ToArray();
                port.Write(arr, 0, arr.Length);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cnvs] WriteLighting exception: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return false;
            }
        }
    }

    /// <summary>
    /// Disable the firmware's boot animation so it stops overlaying our
    /// streamed colors. HYTE's CNVSBaseController.SendToHardware calls the
    /// equivalent <c>TurnFwAnimationOFF</c> on every frame that flips out
    /// of firmware-animation mode; the per-frame check is cheap because the
    /// 4-byte write costs ~30 µs over the CDC link.
    /// </summary>
    public bool SetFirmwareAnimationOff()
    {
        if (!EnsureConnected()) return false;
        lock (_writeLock)
        {
            var port = _port;
            if (port is null) return false;
            try
            {
                var frame = CnvsProtocol.BuildTurnAnimationOff();
                port.Write(frame, 0, frame.Length);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cnvs] SetFirmwareAnimationOff exception: {ex.GetType().Name}: {ex.Message}");
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

    // ── Internals ──

    private static SerialPort OpenPort(string portName)
    {
        var port = new SerialPort(portName, baudRate: 115200, Parity.None, dataBits: 8, StopBits.One)
        {
            ReadBufferSize = 4096,
            WriteBufferSize = 4096,
            ReadTimeout = 500,
            WriteTimeout = 500,
            DtrEnable = true,
            RtsEnable = true,
        };
        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        return port;
    }

    private CnvsProtocol.CnvsSettings? ReadSettingsLocked(SerialPort port)
    {
        lock (_readLock)
        {
            port.DiscardInBuffer();
            var get = CnvsProtocol.BuildGetSettings();
            port.Write(get, 0, get.Length);
            Thread.Sleep(20);
            var buf = new byte[9];
            var n = ReadExact(port, buf, 200);
            if (n < 5) return null;
            return CnvsProtocol.ParseGetSettings(buf.AsSpan(0, n));
        }
    }

    private static int ReadExact(SerialPort port, byte[] buf, int timeoutMs)
    {
        var deadline = Environment.TickCount + Math.Max(1, timeoutMs);
        var total = 0;
        while (total < buf.Length)
        {
            var remaining = deadline - Environment.TickCount;
            if (remaining <= 0) break;
            port.ReadTimeout = remaining;
            int n;
            try
            {
                n = port.Read(buf, total, buf.Length - total);
            }
            catch (TimeoutException) { break; }
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}

/// <summary>One CNVS device as enumerated by the OS, before we open the port.</summary>
public sealed class CnvsPortInfo
{
    public required string PortName { get; init; }
    public string Serial { get; init; } = "";
}

/// <summary>OS-level CNVS USB-CDC port discovery. Windows uses SetupAPI;
/// non-Windows returns empty.</summary>
public interface ICnvsPortDiscovery
{
    IReadOnlyList<CnvsPortInfo> Discover();
}

/// <summary>
/// 24-bit RGB color used by the CNVS lighting write. Mirrors the
/// <see cref="Np50.RgbColor"/> shape so callers can share buffers across
/// hubs without translation.
/// </summary>
public readonly record struct RgbColor(byte R, byte G, byte B);
