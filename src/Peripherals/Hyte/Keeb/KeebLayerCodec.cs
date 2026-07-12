using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Pure helpers for the 0xF2 layer-table page buffer: 8 pages × 65 bytes
/// (byte 0 of each page = report id 0x00, then 64 data bytes) = 512 data
/// bytes = 128 slots × 4-byte key-matrix codes.
///
/// Layer writes always start from the DEVICE'S OWN pristine table (captured
/// via <see cref="KeebHub.ReadLayerRaw"/> before the first Nexus write) and
/// overlay the persisted per-cell assignments onto it. This preserves the
/// factory sentinel at slot 127 (AA AA 55 55, which the vendor app zeroes on
/// any remap) and every vendor-category slot Nexus does not model, and makes
/// reset-to-default a byte-exact factory restore rather than a rebuilt guess.
/// </summary>
public static class KeebLayerCodec
{
    public const int PageCount = KeebProtocol.LayerPageCount; // 8
    public const int PagesBytes = PageCount * KeebLayout.PageSize; // 520
    public const int SlotBytes = 4;

    /// <summary>Copy <paramref name="pristinePages"/> and stamp each overlay's 4-byte code onto its slot.</summary>
    public static byte[] ComposePages(byte[] pristinePages, IEnumerable<(int Slot, byte[] Code)> overlays)
    {
        ArgumentNullException.ThrowIfNull(pristinePages);
        if (pristinePages.Length != PagesBytes)
            throw new ArgumentException($"Layer page buffer must be {PagesBytes} bytes.", nameof(pristinePages));
        var pages = (byte[])pristinePages.Clone();
        foreach (var (slot, code) in overlays) WriteSlot(pages, slot, code);
        return pages;
    }

    /// <summary>Stamp a 4-byte slot code into a page buffer (report-id bytes accounted for).</summary>
    public static void WriteSlot(byte[] pages, int slot, ReadOnlySpan<byte> code)
    {
        if ((uint)slot >= KeebLayerMap.SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot));
        if (code.Length != SlotBytes)
            throw new ArgumentException($"Slot code must be {SlotBytes} bytes.", nameof(code));
        // 16 slots per 64-byte page, so a code never straddles a page boundary.
        var dataOffset = slot * SlotBytes;
        var page = dataOffset / KeebLayout.PageDataSize;
        var index = page * KeebLayout.PageSize + 1 + dataOffset % KeebLayout.PageDataSize;
        code.CopyTo(pages.AsSpan(index, SlotBytes));
    }

    /// <summary>Read a slot's 4-byte code out of a page buffer.</summary>
    public static byte[] ReadSlot(byte[] pages, int slot)
    {
        if ((uint)slot >= KeebLayerMap.SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot));
        var dataOffset = slot * SlotBytes;
        var page = dataOffset / KeebLayout.PageDataSize;
        var index = page * KeebLayout.PageSize + 1 + dataOffset % KeebLayout.PageDataSize;
        return pages.AsSpan(index, SlotBytes).ToArray();
    }
}
