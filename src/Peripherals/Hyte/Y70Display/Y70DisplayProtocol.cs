using System;

namespace Nexus.Service.Peripherals.Hyte.Y70Display;

/// <summary>
/// Pure builders + parsers for the HYTE Y70 Touch display's STM32 controller
/// serial protocol — the controller that the bundled <c>y70*/*.hex</c> images
/// flash. Ported from HYTE's nexus-control-service <c>Y70TouchStm32Commander</c>.
///
/// The Y70 shares the "smart hub" command family with the Q-series/MiniHub, so
/// the firmware-version request is byte-identical (0xFF 0xDD 0x02); the only
/// difference is the Y70 returns a 13-byte response (vs 7), with the version
/// still in bytes [3..6]. Display brightness / on-off uses a separate 0xFF 0xCC
/// command set + DDC/CI and is out of scope here (firmware read only).
/// </summary>
public static class Y70DisplayProtocol
{
    // ── USB identity (cooler-style serial/CDC COM port) ──

    public const int VendorId = 0x3402;
    public const int Y70TouchProductId = 0x0C00;
    public const int Y70InfiniteProductId = 0x0C01;
    public const int Y70TrulyProductId = 0x0C02;

    /// <summary>
    /// Firmware-catalog keys (bundled-.hex directory names). Deliberately none
    /// equal the handler Id ("y70") so an as-yet-unidentified panel maps to a
    /// keyless Id with no bundle (excluded) rather than defaulting to Touch.
    /// </summary>
    public const string VariantTouch = "y70-touch";
    public const string VariantInfinite = "y70-infinite";
    public const string VariantTruly = "y70-truly";

    // ── Wire constants ──

    private const byte Frame0 = 0xFF;
    private const byte OpControl = 0xDD;
    private const byte SubGetFirmwareVersion = 0x02;

    /// <summary>
    /// The Y70 controller answers the version query with a 7-byte frame
    /// (FF DD 02 maj min build hw) — bench-confirmed on a Y70 Touch Infinite
    /// (2026-05-27). HYTE's legacy commander over-allocates a 13-byte read
    /// buffer, but the device only sends 7; the version is in bytes [3..6].
    /// </summary>
    public const int FirmwareVersionResponseLength = 7;

    // ── Builders ──

    public static byte[] BuildGetFirmwareVersion() => new byte[] { Frame0, OpControl, SubGetFirmwareVersion };

    // ── Parsers ──

    /// <summary>
    /// Parse the 13-byte firmware-version response into "Major.Minor.Build.Hw"
    /// (e.g. "1.0.3.1"). Returns empty on a short or mis-echoed reply; the
    /// controller echoes the command header (0xFF 0xDD) in bytes [0..1].
    /// </summary>
    public static string ParseFirmwareVersion(ReadOnlySpan<byte> response)
    {
        if (response.Length < FirmwareVersionResponseLength) return "";
        if (response[0] != Frame0 || response[1] != OpControl) return "";
        return $"{response[3]}.{response[4]}.{response[5]}.{response[6]}";
    }

    /// <summary>Map a matched USB PID to its firmware-catalog variant key, or empty if unrecognized.</summary>
    public static string VariantForProductId(int productId) => productId switch
    {
        Y70TouchProductId => VariantTouch,
        Y70InfiniteProductId => VariantInfinite,
        Y70TrulyProductId => VariantTruly,
        _ => "",
    };

    /// <summary>Operating USB PID for a variant key (for the OTA product key), or -1 if unknown.</summary>
    public static int ProductIdForVariant(string variant) => variant switch
    {
        VariantTouch => Y70TouchProductId,
        VariantInfinite => Y70InfiniteProductId,
        VariantTruly => Y70TrulyProductId,
        _ => -1,
    };
}
