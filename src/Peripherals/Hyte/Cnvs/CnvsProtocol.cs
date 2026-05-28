using System;

namespace Nexus.Service.Peripherals.Hyte.Cnvs;

/// <summary>
/// Pure builders + parsers for the HYTE CNVS firmware-settings wire protocol.
/// Source: HYTE nexus-control-service `LightDancing/Common/CNVSHelper.cs`.
/// The CNVS exposes a small set of single-frame HID commands; there is no
/// heartbeat or polling cadence — the settings are written once on user
/// change and read back on demand.
///
/// Every command starts with <c>0xFF</c> followed by an opcode family byte:
///   <c>0xDC</c> = firmware control (settings, animation enable/disable)
///   <c>0xDD</c> = firmware version query
/// </summary>
public static class CnvsProtocol
{
    // ── USB identity ──

    public const int VendorId = 0x3402;

    /// <summary>
    /// Known CNVS product IDs. Multiple hardware revisions share the same
    /// firmware protocol; the hub opens the first one it finds.
    /// </summary>
    public static readonly int[] ProductIds =
    {
        0x0BFF, // CNVS (CES)
        0x0B00, // CNVS Left / Gen1
        0x0B01, // CNVS v1 / Gen2
        0x0B02, // CNVS White
    };

    /// <summary>
    /// Firmware-catalog variant key for a CNVS product id. CNVS firmware is
    /// variant-specific — Left and v1 ship DIFFERENT images and flashing the
    /// wrong one rewrites the device's USB identity (bench-confirmed) — so each
    /// variant maps to its own bundled-.hex directory. 0BFF (CES) has no bundled
    /// image, so it falls back to "cnvs" (no update offered).
    /// </summary>
    public static string VariantForProductId(int productId) => productId switch
    {
        0x0B00 => "cnvs-left",
        0x0B01 => "cnvs-v1",
        0x0B02 => "cnvs-white",
        _ => "cnvs",
    };

    // ── Wire frame constants ──

    private const byte Frame0 = 0xFF;
    private const byte OpControl = 0xDC;
    private const byte OpQuery = 0xDD;

    private const byte SubAnimOn = 0x02;       // pre-amble for "turn fw animation on"
    private const byte SubAnimToggle = 0x05;   // 0x00 = off, 0x01 = on
    private const byte SubSetSettings = 0x07;  // followed by 2 bytes
    private const byte SubGetSettings = 0x08;  // returns 9 bytes
    private const byte SubGetVersion = 0x02;

    // ── Builders ──

    /// <summary>
    /// Build the "Set CNVS firmware settings" request (5 bytes).
    /// <paramref name="suppressBootAnimation"/> true = boot/connection animation
    /// is silenced (HYTE field <c>TurnOffStartupAnimation</c>).
    /// <paramref name="keepLedsOnWhenPcOff"/> true = LEDs hold their last frame
    /// after a shutdown (HYTE field <c>PlayAnimationWhenPCOff</c>).
    /// </summary>
    public static byte[] BuildSetSettings(bool suppressBootAnimation, bool keepLedsOnWhenPcOff)
    {
        return new byte[]
        {
            Frame0,
            OpControl,
            SubSetSettings,
            suppressBootAnimation ? (byte)0x01 : (byte)0x00,
            keepLedsOnWhenPcOff   ? (byte)0x01 : (byte)0x00,
        };
    }

    /// <summary>
    /// Build the "Get CNVS firmware settings" request (3 bytes). Response is
    /// 9 bytes; bytes 3 / 4 of the response carry the two settings.
    /// </summary>
    public static byte[] BuildGetSettings()
        => new byte[] { Frame0, OpControl, SubGetSettings };

    /// <summary>Build the "Get firmware version" request (3 bytes). Response is 7 bytes.</summary>
    public static byte[] BuildGetFirmwareVersion()
        => new byte[] { Frame0, OpQuery, SubGetVersion };

    /// <summary>Build the "Turn firmware animation off" request (4 bytes).</summary>
    public static byte[] BuildTurnAnimationOff()
        => new byte[] { Frame0, OpControl, SubAnimToggle, 0x00 };

    /// <summary>Build the "Turn firmware animation on" priming request (3 bytes).
    /// HYTE's reference issues this then the on-main command — see
    /// <see cref="BuildTurnAnimationOnMain"/>.</summary>
    public static byte[] BuildTurnAnimationOnPreamble()
        => new byte[] { Frame0, OpControl, SubAnimOn };

    /// <summary>Build the "Turn firmware animation on (main)" request (4 bytes).</summary>
    public static byte[] BuildTurnAnimationOnMain()
        => new byte[] { Frame0, OpControl, SubAnimToggle, 0x01 };

    // ── Parsers ──

    public readonly record struct CnvsSettings(bool SuppressBootAnimation, bool KeepLedsOnWhenPcOff);

    /// <summary>
    /// Parse the 9-byte response to <see cref="BuildGetSettings"/>. Layout
    /// per HYTE's <c>GetCnvsSettingFromFW</c>:
    ///   [3] = TurnOffStartupAnimation (0x01 = boot animation suppressed)
    ///   [4] = PlayAnimationWhenPCOff  (0x01 = LEDs stay on when PC off)
    /// Bytes 0..2 echo the FF DC 08 header; the rest is reserved.
    /// </summary>
    public static CnvsSettings ParseGetSettings(ReadOnlySpan<byte> response)
    {
        if (response.Length < 5)
            throw new ArgumentException($"CNVS get-settings response too short: {response.Length} bytes", nameof(response));
        return new CnvsSettings(
            SuppressBootAnimation: response[3] == 0x01,
            KeepLedsOnWhenPcOff:   response[4] == 0x01);
    }

    /// <summary>Parse the 7-byte firmware-version response. Returns "Major.Minor.Build.Hw" or "" on short read.</summary>
    public static string ParseFirmwareVersion(ReadOnlySpan<byte> response)
    {
        if (response.Length < 7) return "";
        return $"{response[3]}.{response[4]}.{response[5]}.{response[6]}";
    }
}
