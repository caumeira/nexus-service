using System;
using System.Text;

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

    // ── Builders ──

    /// <summary>Build the "Get Firmware Version" request (3 bytes).</summary>
    public static byte[] BuildGetFirmwareVersion() => new byte[] { Frame0, OpControl, SubGetFirmwareVersion };

    /// <summary>Build the "Get Serial Number" request (4 bytes).</summary>
    public static byte[] BuildGetSerial() => new byte[] { Frame0, OpSerialA, OpSerialB, SubGetSerial };

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
