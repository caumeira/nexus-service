using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Encodes a persisted macro into the 4-page (4×65B) 0xF3 payload.
/// Wire layout (hyte-refs firmware-protocol/Keeb/7-macro.md): a 256-byte data
/// stream split across 4 output-report pages (each page byte 0 = report id
/// 0x00, then 64 data bytes). Stream = [Repeat_L, Repeat_H] then action pairs
/// [Attribute, KeyCode], terminated by [0x00, 0x00].
///
/// Attribute bit 7 = press(0)/release(1); bits 0..6 = delay in 10 ms units
/// (1..126). Delay 0x7F is the escape: the following 2-byte "action" is a
/// 16-bit millisecond delay (up to 65535 ms) instead of a key action.
/// </summary>
public static class KeebMacroCodec
{
    public const int PageCount = 4;
    public const int DataBytes = PageCount * KeebLayout.PageDataSize; // 256

    private const byte ReleaseBit = 0x80;
    private const byte ExtendedDelayMarker = 0x7F;
    private const int MaxInlineDelayUnits = 126; // 0x7E; 0x7F is reserved as the escape

    /// <summary>
    /// Build the 4×65B page buffer for <paramref name="macro"/>. Repeat count is
    /// fixed at 1 (the panel records a one-shot sequence). Truncates if the
    /// encoded actions would overflow the 256-byte stream.
    /// </summary>
    public static byte[] BuildMacroPages(KeebMacroDocument macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        var data = new byte[DataBytes];
        data[0] = 0x01; // Repeat_L = 1
        data[1] = 0x00; // Repeat_H
        var pos = 2;

        foreach (var k in macro.Keys)
        {
            var hid = KeebKeyCodes.MacroHid(k.Key);
            if (hid == 0) continue; // unmappable key - skip rather than emit a stray action

            var release = IsRelease(k.Type);
            var units = (int)Math.Round(Math.Max(0, k.Duration) / 10.0);

            if (units <= MaxInlineDelayUnits)
            {
                if (pos + 2 > DataBytes - 2) break; // leave room for the [00,00] terminator
                data[pos++] = (byte)((release ? ReleaseBit : 0) | (byte)units);
                data[pos++] = hid;
            }
            else
            {
                // Inline delay can't hold it: emit the action with the 0x7F escape,
                // then a 16-bit ms delay as the next 2-byte slot.
                if (pos + 4 > DataBytes - 2) break;
                data[pos++] = (byte)((release ? ReleaseBit : 0) | ExtendedDelayMarker);
                data[pos++] = hid;
                var ms = Math.Min(k.Duration, ushort.MaxValue);
                data[pos++] = (byte)(ms & 0xFF);
                data[pos++] = (byte)((ms >> 8) & 0xFF);
            }
        }
        // [00,00] terminator is already in place (buffer zero-initialised).

        // Split into 4 pages, each prefixed with the 0x00 report id.
        var pages = new byte[PageCount * KeebLayout.PageSize];
        for (var p = 0; p < PageCount; p++)
        {
            var pageBase = p * KeebLayout.PageSize;
            // pages[pageBase] = report id 0x00 (already zero)
            Array.Copy(data, p * KeebLayout.PageDataSize, pages, pageBase + 1, KeebLayout.PageDataSize);
        }
        return pages;
    }

    private static bool IsRelease(string? type) => (type ?? "").Trim().ToLowerInvariant() switch
    {
        "break" or "keyup" or "release" => true,
        _ => false, // Make / KeyDown / press
    };
}
