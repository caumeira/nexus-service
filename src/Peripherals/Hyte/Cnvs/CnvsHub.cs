using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;

namespace Nexus.Service.Peripherals.Hyte.Cnvs;

/// <summary>
/// Talks to a HYTE CNVS over a USB-CDC virtual COM port.
///
/// The CNVS family (Left/Gen1 PID 0x0B00, v1/Gen2 PID 0x0B01, White
/// PID 0x0B02, CES PID 0x0BFF) enumerates as a serial port, NOT a
/// HID device — Y70's CNVS Left is `USB Serial Device (COM7)` per
/// PnP enumeration. HYTE's `CNVSLeftController` and `CNVSV1Controller`
/// both open a <c>SerialStream</c> at 115200 8N1; this hub matches.
///
/// We open the port on demand and close it after each call. The
/// CNVS firmware-settings surface is write-on-change with rare
/// read-backs — no heartbeat, no streaming. Holding the port open
/// would block other tools (HYTE Nexus 2.0 still being used
/// side-by-side, OpenRGB-headless, etc.) for no benefit.
/// </summary>
public sealed class CnvsHub
{
    private readonly ICnvsPortDiscovery _discovery;
    private readonly object _lock = new();

    public CnvsHub(ICnvsPortDiscovery discovery)
    {
        _discovery = discovery;
    }

    public bool IsConnected
    {
        get
        {
            var ports = _discovery.Discover();
            return ports.Count > 0;
        }
    }

    /// <summary>
    /// Push both firmware settings in one 5-byte frame. Read-back to
    /// verify the firmware accepted them — `SerialPort.Write` returning
    /// only proves the OS accepted the bytes, not the device. Returns
    /// true when the read-back matches OR when the write succeeded but
    /// the device didn't echo (some CNVS firmwares only echo after a
    /// USB renum); only returns false on hard transport errors.
    /// </summary>
    public bool WriteSettings(bool suppressBootAnimation, bool keepLedsOnWhenPcOff)
    {
        lock (_lock)
        {
            var ports = _discovery.Discover();
            if (ports.Count == 0)
            {
                Console.Error.WriteLine("[cnvs] WriteSettings: no CNVS serial port found");
                return false;
            }
            var info = ports[0];
            try
            {
                using var port = OpenPort(info.PortName);
                var frame = CnvsProtocol.BuildSetSettings(suppressBootAnimation, keepLedsOnWhenPcOff);
                port.Write(frame, 0, frame.Length);
                Console.Error.WriteLine(
                    $"[cnvs] WriteSettings sent {frame.Length}B to {info.PortName} (serial={info.Serial}): boot={suppressBootAnimation} leds={keepLedsOnWhenPcOff}");

                Thread.Sleep(20);

                var readBack = TryReadSettings(port);
                if (readBack is { } rb)
                {
                    if (rb.SuppressBootAnimation == suppressBootAnimation
                        && rb.KeepLedsOnWhenPcOff == keepLedsOnWhenPcOff)
                    {
                        Console.Error.WriteLine($"[cnvs] WriteSettings read-back matches: {rb}");
                    }
                    else
                    {
                        Console.Error.WriteLine(
                            $"[cnvs] WriteSettings read-back disagrees — wanted boot={suppressBootAnimation} leds={keepLedsOnWhenPcOff}, got {rb}");
                    }
                }
                else
                {
                    Console.Error.WriteLine("[cnvs] WriteSettings: no read-back data");
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cnvs] WriteSettings exception on {info.PortName}: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    public CnvsProtocol.CnvsSettings? ReadSettings()
    {
        lock (_lock)
        {
            var ports = _discovery.Discover();
            if (ports.Count == 0) return null;
            var info = ports[0];
            try
            {
                using var port = OpenPort(info.PortName);
                return TryReadSettings(port);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cnvs] ReadSettings exception on {info.PortName}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }

    public string GetFirmwareVersion()
    {
        lock (_lock)
        {
            var ports = _discovery.Discover();
            if (ports.Count == 0) return "";
            var info = ports[0];
            try
            {
                using var port = OpenPort(info.PortName);
                port.DiscardInBuffer();
                var frame = CnvsProtocol.BuildGetFirmwareVersion();
                port.Write(frame, 0, frame.Length);
                Thread.Sleep(20);
                var buf = new byte[7];
                var n = ReadExact(port, buf, 200);
                if (n < 7) return "";
                return CnvsProtocol.ParseFirmwareVersion(buf);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cnvs] GetFirmwareVersion exception: {ex.GetType().Name}: {ex.Message}");
                return "";
            }
        }
    }

    // ── Internals ──

    /// <summary>
    /// Open a CNVS serial port at 115200 8N1 with DTR/RTS asserted.
    /// HYTE's CNVSLeftController and CNVSV1Controller both use 115200;
    /// the DTR/RTS bits are required by some Windows CDC drivers to
    /// actually deliver data even though the CNVS firmware ignores them.
    /// </summary>
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

    private static CnvsProtocol.CnvsSettings? TryReadSettings(SerialPort port)
    {
        port.DiscardInBuffer();
        var get = CnvsProtocol.BuildGetSettings();
        port.Write(get, 0, get.Length);
        Thread.Sleep(20);
        // HYTE's CNVSHelper.GetCnvsSettingFromFW reads exactly 9 bytes.
        // The two flags live at byte offsets 3 (TurnOffStartupAnimation)
        // and 4 (PlayAnimationWhenPCOff) of the response.
        var buf = new byte[9];
        var n = ReadExact(port, buf, 200);
        if (n < 5) return null;
        return CnvsProtocol.ParseGetSettings(buf.AsSpan(0, n));
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
            catch (TimeoutException)
            {
                break;
            }
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

/// <summary>OS-level discovery for CNVS USB-CDC ports. Windows uses SetupAPI;
/// non-Windows returns empty.</summary>
public interface ICnvsPortDiscovery
{
    IReadOnlyList<CnvsPortInfo> Discover();
}
