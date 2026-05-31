using System;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// LED layout for the HYTE Keeb TKL, ported from the shipping OpenRGB
/// controller (nexus-rgb/openrgb-headless RGBController_HYTEKeyboard.cpp) so
/// our direct-HID stream is byte-for-byte equivalent to the path that
/// already lights the board.
///
/// The keyboard zone is addressed on the wire by each key's firmware LED
/// "value" (0..121, sparse — not every index is a physical key). The stream
/// is 6 pages × 64 payload bytes = 384 bytes = 128 RGB triplets; physical
/// keys live at the <see cref="KeyWireValues"/> indices and every other slot
/// is left black (no LED is wired there, so the byte is a no-op). The
/// underglow zone is a flat 63-LED linear strip streamed as 3 pages.
/// </summary>
public static class KeebLayout
{
    // ── Stream geometry (matches OpenRGB HYTEKeyboardController) ──

    /// <summary>Bytes per HID output report page: 1 report-id byte + 64 payload.</summary>
    public const int PageSize = 65;

    /// <summary>RGB payload bytes per page (page byte 0 is the report id).</summary>
    public const int PageDataSize = 64;

    /// <summary>Pages in the keyboard (middle) RGB stream.</summary>
    public const int KeyPageCount = 6;

    /// <summary>Pages in the underglow (surround) RGB stream.</summary>
    public const int SurroundPageCount = 3;

    /// <summary>
    /// RGB triplets carried by the keyboard stream (6 × 64 / 3 = 128). The wire
    /// buffer is sized to this; physical keys occupy <see cref="KeyWireValues"/>.
    /// </summary>
    public const int KeyWireSlots = KeyPageCount * PageDataSize / 3; // 128

    /// <summary>RGB triplets carried by the underglow stream (3 × 64 / 3 = 64).</summary>
    public const int SurroundWireSlots = SurroundPageCount * PageDataSize / 3; // 64

    /// <summary>Physical underglow LEDs (OpenRGB surround zone: 63).</summary>
    public const int SurroundLedCount = 63;

    /// <summary>
    /// Physical keyboard LED "values" in firmware index order (ascending).
    /// Source: RGBController_HYTEKeyboard.cpp — the 89-entry base list
    /// <c>hyte_keeb_tkl_values</c> PLUS the 9 edit-key LEDs merged in by
    /// <c>ChangeKeys(edit_keys)</c> (media row 77/78/79/98/100 and the four
    /// spacebar-underglow LEDs 109/110/112/113). 89 + 9 = 98 = the board's
    /// real key count (== OpenRGB's GetKeyCount() after ChangeKeys). The
    /// engine drives the keyboard zone as a linear strip of this many LEDs;
    /// LED i is streamed to wire slot KeyWireValues[i].
    /// </summary>
    public static readonly int[] KeyWireValues =
    {
        0, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37,
        42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58,
        63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79,
        84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 97, 98, 99, 100,
        105, 106, 107, 109, 110, 111, 112, 113, 115, 116, 117, 118, 119, 120, 121,
    };

    /// <summary>Physical keyboard key count (== <see cref="KeyWireValues"/> length).</summary>
    public static int KeyLedCount => KeyWireValues.Length; // 98

    /// <summary>
    /// Scatter <paramref name="ledOrder"/> (one color per physical key, in
    /// <see cref="KeyWireValues"/> order) into the 128-slot wire buffer the
    /// firmware streams. Clears <paramref name="wire"/> first, so slots without
    /// a physical LED are streamed as black (a no-op for unwired indices).
    /// </summary>
    public static void MapKeysToWire(ReadOnlySpan<RgbColor> ledOrder, Span<RgbColor> wire)
    {
        if (wire.Length < KeyWireSlots)
            throw new ArgumentException($"Wire buffer must be at least {KeyWireSlots} slots.", nameof(wire));
        wire.Clear();
        var n = Math.Min(ledOrder.Length, KeyWireValues.Length);
        for (var i = 0; i < n; i++)
        {
            var slot = KeyWireValues[i];
            if ((uint)slot < (uint)wire.Length) wire[slot] = ledOrder[i];
        }
    }
}
