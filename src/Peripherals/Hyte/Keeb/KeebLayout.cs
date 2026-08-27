using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// LED layout for the HYTE Keeb TKL.
///
/// Two coordinate systems matter here and they are NOT the same thing:
///
/// * The <b>wire slot</b> is where a colour goes in the HID stream. Keys are
///   scattered into a 128-slot buffer at their firmware LED value; underglow
///   LEDs occupy the first <see cref="SurroundLedCount"/> slots of their own
///   64-slot buffer.
/// * The <b>board grid</b> is where the LED physically sits, as a cell in a
///   <see cref="BoardGridWidth"/> x <see cref="BoardGridHeight"/> grid that
///   spans the whole keyboard - underglow edges included. This is what stock
///   per-LED positions are derived from.
///
/// A wire slot decoded as a row/column of a 21-wide matrix yields the firmware
/// SCAN matrix, which is a different thing again: it puts the five media keys
/// out at columns 14-16 of the function row instead of under the scroll wheel,
/// and it drops every gap between key groups. Ground truth for the board grid
/// is the shipping HYTE app's <c>KeebCommon</c> layout lists, whose
/// <c>LedPosition(Y, X, CommandsIndex)</c> carries grid row, grid column and
/// the 1-based wire slot together.
/// </summary>
public static class KeebLayout
{
    // -- Stream geometry (matches OpenRGB HYTEKeyboardController) --

    /// <summary>Bytes per HID output report page: 1 report-id byte + 64 payload.</summary>
    public const int PageSize = 65;

    /// <summary>RGB payload bytes per page (page byte 0 is the report id).</summary>
    public const int PageDataSize = 64;

    /// <summary>Pages in the keyboard (middle) RGB stream.</summary>
    public const int KeyPageCount = 6;

    /// <summary>Pages in the underglow (surround) RGB stream.</summary>
    public const int SurroundPageCount = 3;

    /// <summary>
    /// RGB triplets carried by the keyboard stream (6 x 64 / 3 = 128). The wire
    /// buffer is sized to this; physical keys occupy the slots listed by the
    /// active <see cref="KeebKeyMap"/>.
    /// </summary>
    public const int KeyWireSlots = KeyPageCount * PageDataSize / 3; // 128

    /// <summary>RGB triplets carried by the underglow stream (3 x 64 / 3 = 64).</summary>
    public const int SurroundWireSlots = SurroundPageCount * PageDataSize / 3; // 64

    // -- Board grid (shared by keys and underglow) --

    /// <summary>Columns in the board grid (x 0..20). Column 0 and 20 are the underglow edges.</summary>
    public const int BoardGridWidth = 21;

    /// <summary>Rows in the board grid (y 0..9). Row 0 and 9 are the underglow edges.</summary>
    public const int BoardGridHeight = 10;

    /// <summary>Normalize a board-grid cell onto the unit square.</summary>
    public static (float U, float V) GridToUv(int column, int row) =>
        (column / (float)(BoardGridWidth - 1), row / (float)(BoardGridHeight - 1));

    // -- Underglow (perimeter) layout --

    /// <summary>
    /// Physical underglow LEDs. OpenRGB declares a flat 63 for this zone, but
    /// only wire slots 0..50 are populated by firmware: the board perimeter is
    /// 19 top + 6 left + 19 bottom + 6 right = 50 LEDs plus the scroll-wheel
    /// ring, and slots 51..63 drive nothing.
    /// </summary>
    public const int SurroundLedCount = 51;

    /// <summary>
    /// Underglow LED positions in wire order, as (column, row) board-grid
    /// cells. The strip is a single counter-clockwise ring seamed at top
    /// centre: it starts just left of centre on the top edge and runs LEFT to
    /// the top-left corner (slots 0..9), down the left edge (10..15),
    /// left-to-right along the bottom (16..34), up the right edge (35..40),
    /// then right-to-left along the top-right span back to centre (41..49).
    /// Slot 50 is the scroll wheel, which is not on the perimeter at all.
    ///
    /// This is why an evenly-distributed clockwise-from-top-left perimeter
    /// walk (the previous default) put every underglow LED in the wrong place:
    /// wrong start, wrong direction, wrong count, and it had no way to know
    /// slot 50 is off the ring.
    /// </summary>
    public static readonly (int Column, int Row)[] SurroundGrid = BuildSurroundGrid();

    private static (int Column, int Row)[] BuildSurroundGrid()
    {
        var columns = new[]
        {
        10, 9, 8, 7, 6, 5, 4, 3,
        2, 1, 0, 0, 0, 0, 0, 0,
        1, 2, 3, 4, 5, 6, 7, 8,
        9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 20, 20, 20, 20,
        20, 19, 18, 17, 16, 15, 14, 13,
        12, 11, 2,
        };
        var rows = new[]
        {
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 3, 4, 5, 6, 7, 8,
        9, 9, 9, 9, 9, 9, 9, 9,
        9, 9, 9, 9, 9, 9, 9, 9,
        9, 9, 9, 8, 7, 6, 5, 4,
        3, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 1,
        };
        var grid = new (int, int)[SurroundLedCount];
        for (var i = 0; i < SurroundLedCount; i++) grid[i] = (columns[i], rows[i]);
        return grid;
    }

    /// <summary>
    /// Stock per-LED positions for the underglow zone: each wire slot's
    /// <see cref="SurroundGrid"/> cell normalized onto the unit square.
    /// </summary>
    public static (float[] U, float[] V) ComputeSurroundUv()
    {
        var u = new float[SurroundLedCount];
        var v = new float[SurroundLedCount];
        for (var i = 0; i < SurroundLedCount; i++)
        {
            (u[i], v[i]) = GridToUv(SurroundGrid[i].Column, SurroundGrid[i].Row);
        }
        return (u, v);
    }
}

/// <summary>
/// The keyboard zone's LED table for one physical layout. Key count and per-key
/// board position both differ between ANSI and ISO: ISO adds Europe1 (wire slot
/// 75) and Europe2 (85), drops BackSlash (55), and seats Enter a row higher.
/// Selected from the firmware-reported layout in <see cref="KeebState.Layout"/>.
/// </summary>
public sealed class KeebKeyMap
{
    /// <summary>Largest <see cref="LedCount"/> across all layouts - the size a shared scratch buffer needs.</summary>
    public const int MaxLedCount = 97;

    /// <summary>"ANSI" or "ISO".</summary>
    public string Layout { get; }

    /// <summary>
    /// Wire slots in ascending order; LED i of the keys zone streams to
    /// <c>WireValues[i]</c>. Ascending order is the LED ordering the zone and
    /// every stored partition index against.
    /// </summary>
    public int[] WireValues { get; }

    /// <summary>Board-grid column per LED, parallel to <see cref="WireValues"/>.</summary>
    public int[] Columns { get; }

    /// <summary>Board-grid row per LED, parallel to <see cref="WireValues"/>.</summary>
    public int[] Rows { get; }

    /// <summary>Physical key LEDs on this layout.</summary>
    public int LedCount => WireValues.Length;

    private KeebKeyMap(string layout, int[] wireValues, int[] columns, int[] rows)
    {
        Layout = layout;
        WireValues = wireValues;
        Columns = columns;
        Rows = rows;
    }

    /// <summary>LED index for a wire slot, or -1 when this layout has no LED there.</summary>
    public int IndexOfWireValue(int wireValue) => Array.IndexOf(WireValues, wireValue);

    /// <summary>
    /// Stock per-key positions: each LED's board-grid cell normalized onto the
    /// unit square. Keys share the underglow's coordinate space, so a
    /// device-wide effect lines the two zones up instead of stretching each to
    /// fill the canvas on its own.
    /// </summary>
    public (float[] U, float[] V) ComputeUv()
    {
        var u = new float[LedCount];
        var v = new float[LedCount];
        for (var i = 0; i < LedCount; i++)
        {
            (u[i], v[i]) = KeebLayout.GridToUv(Columns[i], Rows[i]);
        }
        return (u, v);
    }

    /// <summary>
    /// Scatter <paramref name="ledOrder"/> (one color per physical key, in
    /// <see cref="WireValues"/> order) into the 128-slot wire buffer the
    /// firmware streams. Clears <paramref name="wire"/> first, so slots without
    /// a physical LED are streamed as black (a no-op for unwired indices).
    /// </summary>
    public void MapKeysToWire(ReadOnlySpan<RgbColor> ledOrder, Span<RgbColor> wire)
    {
        if (wire.Length < KeebLayout.KeyWireSlots)
            throw new ArgumentException($"Wire buffer must be at least {KeebLayout.KeyWireSlots} slots.", nameof(wire));
        wire.Clear();
        var n = Math.Min(ledOrder.Length, LedCount);
        for (var i = 0; i < n; i++)
        {
            var slot = WireValues[i];
            if ((uint)slot < (uint)wire.Length) wire[slot] = ledOrder[i];
        }
    }

    /// <summary>ANSI board: 96 key LEDs.</summary>
    public static readonly KeebKeyMap Ansi = new("ANSI",
        new[]
        {
        0, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36,
        37, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56,
        57, 58, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 76, 77,
        78, 79, 84, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 97, 98, 99,
        100, 105, 106, 107, 109, 110, 111, 112, 113, 115, 116, 117, 118, 119, 120, 121,
        },
        new[]
        {
        1, 3, 4, 5, 6, 8, 9, 10, 11, 13, 14, 15, 16, 17, 18, 19,
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 17, 18,
        19, 1, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 17,
        18, 19, 1, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 1,
        2, 3, 2, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 4, 18,
        5, 1, 2, 3, 5, 6, 7, 8, 9, 12, 13, 14, 15, 17, 18, 19,
        },
        new[]
        {
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
        5, 5, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 2,
        2, 2, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 2, 7,
        2, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
        });

    /// <summary>ISO board: 97 key LEDs (ANSI plus Europe1 / Europe2, minus BackSlash).</summary>
    public static readonly KeebKeyMap Iso = new("ISO",
        new[]
        {
        0, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36,
        37, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 56, 57,
        58, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77,
        78, 79, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 97, 98,
        99, 100, 105, 106, 107, 109, 110, 111, 112, 113, 115, 116, 117, 118, 119, 120,
        121,
        },
        new[]
        {
        1, 3, 4, 5, 6, 8, 9, 10, 11, 13, 14, 15, 16, 17, 18, 19,
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 17, 18,
        19, 1, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 17, 18,
        19, 1, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 1,
        2, 3, 2, 1, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 4,
        18, 5, 1, 2, 3, 5, 6, 7, 8, 9, 12, 13, 14, 15, 17, 18,
        19,
        },
        new[]
        {
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
        5, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 5, 2,
        2, 2, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 2,
        7, 2, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
        8,
        });

    /// <summary>
    /// Map a firmware-reported layout string to its table. Anything other than
    /// "ISO" falls back to ANSI, matching <see cref="KeebState.Layout"/>'s own
    /// pre-device-info default.
    /// </summary>
    public static KeebKeyMap ForLayout(string? layout) =>
        string.Equals(layout, "ISO", StringComparison.OrdinalIgnoreCase) ? Iso : Ansi;

    /// <summary>Both tables, for tests and structure enumeration.</summary>
    public static IReadOnlyList<KeebKeyMap> All { get; } = new[] { Ansi, Iso };
}
