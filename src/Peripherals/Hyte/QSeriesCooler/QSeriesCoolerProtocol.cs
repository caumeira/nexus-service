using System;
using System.Text;
using Nexus.Service.Peripherals.Hyte.MiniHub; // RgbColor: shared HYTE serial RGB triple

namespace Nexus.Service.Peripherals.Hyte.QSeriesCooler;

/// <summary>
/// Pure builders + parsers for the HYTE Q-series (Q60 / Q80 "THICC" AIO)
/// cooler-controller serial protocol — the STM32 controller that the bundled
/// <c>q60/*.hex</c> / <c>q80/*.hex</c> images flash. This is NOT the Android
/// LCD panel (that's the ADB-based <c>src/QSeries/</c> stack); the cooler
/// controller enumerates as a separate USB-CDC virtual COM port
/// (VID 3402, PID 0400 = Q60, PID 0403 = Q80).
///
/// Ported from HYTE's nexus-control-service <c>SmartHubCommandBase</c>. The
/// Q-series shares the "smart hub" command family with the MiniHub, so the
/// firmware-version exchange is byte-identical to
/// <c>MiniHubProtocol.BuildGetFirmwareVersion</c> (0xFF 0xDD 0x02 → 7 bytes,
/// version = bytes [3..6]).
/// </summary>
public static class QSeriesCoolerProtocol
{
    // ── USB identity ──

    public const int VendorId = 0x3402;
    public const int Q60ProductId = 0x0400;
    public const int Q80ProductId = 0x0403;

    /// <summary>Firmware-catalog keys (bundled-.hex directory names).</summary>
    public const string VariantQ60 = "q60";
    public const string VariantQ80 = "q80";

    /// <summary>Operating USB PID for a variant key (for the OTA product key), or -1 if unknown.</summary>
    public static int ProductIdForVariant(string variant) => variant switch
    {
        VariantQ60 => Q60ProductId,
        VariantQ80 => Q80ProductId,
        _ => -1,
    };

    // ── Wire constants ──

    private const byte Frame0 = 0xFF;
    private const byte OpControl = 0xDD;        // version / mode / fan / get
    private const byte OpSerialA = 0xAA;        // serial-number query prefix
    private const byte OpSerialB = 0xBB;
    private const byte SubGetFirmwareVersion = 0x02;
    private const byte SubGetSerial = 0x02;

    public const int FirmwareVersionResponseLength = 7;
    public const int SerialResponseLength = 37;

    // ── Lighting wire constants ──
    //
    // Mirrors the legacy PQSeriesDeviceBase.SendToHardware flow: enable software
    // RGB control, then stream each of the 4 LED ports as a fixed 90-byte frame.
    // Bytes after the 7-byte header are G,R,B triples (HYTE firmware expects GRB,
    // same as MiniHub/NP50). The two LED-count header bytes are the hardcoded
    // magic 0x01 0x68 the reference agent always emits regardless of real count.
    private const byte OpLighting = 0xEE;          // LED streaming
    private const byte SubStreaming = 0x01;        // Q-series stream sub-op (MiniHub uses 0x03)
    private const byte SubSetRgbControlMode = 0x03;
    private const byte LedCountMagicHigh = 0x01;
    private const byte LedCountMagicLow = 0x68;

    public const byte RgbModeSoftware = 0x00;      // nexus drives the LEDs
    public const byte RgbModeMotherboard = 0x01;   // hand off to the mobo ARGB header (power-on default)

    /// <summary>The Q-series cooler hub streams over 4 LED ports (pump head + fan/strip channels).</summary>
    public const int LedPortCount = 4;

    /// <summary>Every streamed port frame is exactly this many bytes: 7-byte header + zero-padded GRB data.</summary>
    public const int StreamFrameLength = 90;

    /// <summary>Max LEDs carried in one port frame = floor((90 - 7) / 3).</summary>
    public const int MaxLedsPerPort = (StreamFrameLength - 7) / 3; // 27

    // ── Builders ──

    /// <summary>Build the "Get Firmware Version" request (3 bytes).</summary>
    public static byte[] BuildGetFirmwareVersion() => new byte[] { Frame0, OpControl, SubGetFirmwareVersion };

    /// <summary>Build the "Get Serial Number" request (4 bytes).</summary>
    public static byte[] BuildGetSerial() => new byte[] { Frame0, OpSerialA, OpSerialB, SubGetSerial };

    /// <summary>
    /// Build the "Set RGB Control Mode" request (4 bytes). <see cref="RgbModeSoftware"/> gives
    /// nexus full control of the LEDs; <see cref="RgbModeMotherboard"/> hands off to the
    /// motherboard ARGB header (the default after a power cycle). Software mode must be set
    /// before any <see cref="BuildLightingStream"/> write actually reaches the LEDs.
    /// </summary>
    public static byte[] BuildSetRgbControlMode(byte mode)
    {
        if (mode != RgbModeSoftware && mode != RgbModeMotherboard)
            throw new ArgumentException($"Unknown RGB control mode 0x{mode:X2}", nameof(mode));
        return new byte[] { Frame0, OpControl, SubSetRgbControlMode, mode };
    }

    /// <summary>
    /// Build a fixed 90-byte LED streaming frame for one port (1..<see cref="LedPortCount"/>).
    /// Header is <c>FF EE 01 &lt;port&gt; 01 68 00</c>; the remaining bytes are G,R,B triples,
    /// zero-padded past the supplied LED count so trailing/disconnected LEDs go dark. LEDs past
    /// <see cref="MaxLedsPerPort"/> are dropped to keep the frame exactly <see cref="StreamFrameLength"/>.
    /// Matches legacy PQSeriesDeviceBase.SendToHardware (4 ports, PadListWithZeros(90), GRB order).
    /// </summary>
    public static byte[] BuildLightingStream(int port, ReadOnlySpan<RgbColor> leds)
    {
        if (port < 1 || port > LedPortCount)
            throw new ArgumentOutOfRangeException(nameof(port), port, $"Port must be in 1..{LedPortCount}.");
        var buf = new byte[StreamFrameLength];
        buf[0] = Frame0;
        buf[1] = OpLighting;
        buf[2] = SubStreaming;
        buf[3] = (byte)port;
        buf[4] = LedCountMagicHigh;
        buf[5] = LedCountMagicLow;
        // buf[6] reserved (0)
        var count = Math.Min(leds.Length, MaxLedsPerPort);
        for (var i = 0; i < count; i++)
        {
            var off = 7 + i * 3;
            buf[off + 0] = leds[i].G;
            buf[off + 1] = leds[i].R;
            buf[off + 2] = leds[i].B;
        }
        return buf;
    }

    // ── Parsers ──

    /// <summary>
    /// Parse the 7-byte firmware-version response into "Major.Minor.Build.Hw"
    /// (e.g. "2.0.9.1"). Returns empty on a short or mis-echoed reply. The
    /// firmware echoes the command header (0xFF 0xDD) in bytes [0..1].
    /// </summary>
    public static string ParseFirmwareVersion(ReadOnlySpan<byte> response)
    {
        if (response.Length < FirmwareVersionResponseLength) return "";
        if (response[0] != Frame0 || response[1] != OpControl) return "";
        return $"{response[3]}.{response[4]}.{response[5]}.{response[6]}";
    }

    /// <summary>
    /// Parse the 37-byte serial-number response. The serial is a UTF-8 string
    /// in bytes [4..30); the legacy treats 0xFF in the trailing bytes as
    /// "unset" and returns empty.
    /// </summary>
    public static string ParseSerial(ReadOnlySpan<byte> response)
    {
        if (response.Length < SerialResponseLength) return "";
        if (response[0] != Frame0 || response[1] != OpSerialA) return "";
        if (response[30] == 0xFF && response[31] == 0xFF) return "";
        return Encoding.UTF8.GetString(response.Slice(4, 26)).TrimEnd('\0');
    }
}
