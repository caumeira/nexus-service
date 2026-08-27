using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// Stock per-LED positions for the keeb: key UVs derived from the firmware
/// board grid per layout (ANSI and ISO differ in count and placement),
/// underglow from the same grid (a counter-clockwise ring seamed at top
/// centre, plus the off-ring scroll wheel), and per-zone seeds as the concatenation
/// of each zone's slice spans over the segment defaults - for the default
/// partition and any custom partition shape alike.
/// </summary>
public class KeebStockUvTests
{
    private const string HubId = "keeb:SER123";

    // ── Key board-grid derivation ──

    [Fact]
    public void Key_uv_count_matches_physical_key_count()
    {
        foreach (var keys in KeebKeyMap.All)
        {
            var (u, v) = keys.ComputeUv();
            Assert.Equal(keys.LedCount, u.Length);
            Assert.Equal(keys.LedCount, v.Length);
        }
    }

    [Fact]
    public void Layout_key_counts_match_the_firmware_tables()
    {
        Assert.Equal(96, KeebKeyMap.Ansi.LedCount);
        Assert.Equal(97, KeebKeyMap.Iso.LedCount);
        // ISO adds Europe1 / Europe2 and drops BackSlash.
        Assert.True(KeebKeyMap.Iso.IndexOfWireValue(75) >= 0);
        Assert.True(KeebKeyMap.Iso.IndexOfWireValue(85) >= 0);
        Assert.Equal(-1, KeebKeyMap.Iso.IndexOfWireValue(55));
        Assert.Equal(-1, KeebKeyMap.Ansi.IndexOfWireValue(75));
        Assert.Equal(-1, KeebKeyMap.Ansi.IndexOfWireValue(85));
        Assert.True(KeebKeyMap.Ansi.IndexOfWireValue(55) >= 0);
        // MaxLedCount sizes the shared reactive scratch buffers, so it must cover
        // EVERY layout, not just the one that happens to be largest today.
        foreach (var map in KeebKeyMap.All)
            Assert.True(KeebKeyMap.MaxLedCount >= map.LedCount, $"{map.Layout} exceeds MaxLedCount");
    }

    [Fact]
    public void ForLayout_selects_the_table_and_defaults_to_ansi()
    {
        Assert.Same(KeebKeyMap.Iso, KeebKeyMap.ForLayout("ISO"));
        Assert.Same(KeebKeyMap.Iso, KeebKeyMap.ForLayout("iso"));
        Assert.Same(KeebKeyMap.Ansi, KeebKeyMap.ForLayout("ANSI"));
        Assert.Same(KeebKeyMap.Ansi, KeebKeyMap.ForLayout(null));
        Assert.Same(KeebKeyMap.Ansi, KeebKeyMap.ForLayout(""));
    }

    [Fact]
    public void Wire_values_are_unique_ascending_and_in_range()
    {
        foreach (var keys in KeebKeyMap.All)
        {
            var v = keys.WireValues;
            Assert.Equal(keys.LedCount, keys.Columns.Length);
            Assert.Equal(keys.LedCount, keys.Rows.Length);
            for (var i = 1; i < v.Length; i++)
                Assert.True(v[i] > v[i - 1], $"{keys.Layout} values must be strictly ascending at {i}");
            Assert.All(v, x => Assert.InRange(x, 0, KeebLayout.KeyWireSlots - 1));
            Assert.Equal(121, v[^1]); // last physical LED value on both layouts
            Assert.Equal(-1, keys.IndexOfWireValue(1)); // value 1 is an unwired gap
        }
    }

    [Fact]
    public void Keys_sit_inside_the_underglow_ring_on_the_shared_board_grid()
    {
        // Keys occupy grid columns 1..19 and rows 2..8; the underglow owns
        // column 0/20 and row 0/9. Sharing one grid is what lets a device-wide
        // effect line the two zones up.
        foreach (var keys in KeebKeyMap.All)
        {
            for (var i = 0; i < keys.LedCount; i++)
            {
                Assert.InRange(keys.Columns[i], 1, KeebLayout.BoardGridWidth - 2);
                Assert.InRange(keys.Rows[i], 2, KeebLayout.BoardGridHeight - 2);
            }
        }
    }

    [Fact]
    public void Media_keys_sit_in_a_row_directly_under_the_scroll_wheel()
    {
        // Wire values 77, 78, 79, 98, 100 are MediaLED1..5. Decoding them as a
        // stride-21 matrix yields the firmware SCAN position (columns 14-16 of
        // the function row), which is where they used to be placed. Physically
        // they are their own row at the top left, under the wheel.
        var wheel = KeebLayout.SurroundGrid[50];
        Assert.Equal((2, 1), wheel);

        var expected = new[] { (77, 1), (78, 2), (79, 3), (98, 4), (100, 5) };
        foreach (var keys in KeebKeyMap.All)
        {
            foreach (var (wireValue, column) in expected)
            {
                var i = keys.IndexOfWireValue(wireValue);
                Assert.True(i >= 0, $"{keys.Layout} is missing media wire value {wireValue}");
                Assert.Equal(column, keys.Columns[i]);
                Assert.Equal(wheel.Row + 1, keys.Rows[i]);
            }
        }
    }

    [Theory]
    // Ground-truth board cells from the shipping HYTE app's KeebCommon.ANSILayout.
    // Every one of these differs from the stride-21 scan-matrix decode this
    // replaced, so the set fails loudly if the tables ever regress to it.
    [InlineData(0, 1, 3)]     // ESC
    [InlineData(6, 8, 3)]     // F5   - scan matrix said column 6
    [InlineData(43, 3, 5)]    // Q    - scan matrix said column 1
    [InlineData(56, 17, 5)]   // Del  - scan matrix said column 14
    [InlineData(77, 1, 2)]    // MediaLED1 - scan matrix said column 14, row 3
    [InlineData(121, 19, 8)]  // Right arrow
    public void Ansi_keys_sit_on_their_firmware_board_cell(int wireValue, int column, int row)
    {
        var keys = KeebKeyMap.Ansi;
        var i = keys.IndexOfWireValue(wireValue);
        Assert.True(i >= 0);
        Assert.Equal(column, keys.Columns[i]);
        Assert.Equal(row, keys.Rows[i]);

        var (u, v) = keys.ComputeUv();
        Assert.Equal(column / 20f, u[i]);
        Assert.Equal(row / 9f, v[i]);
    }

    [Fact]
    public void Escape_and_right_arrow_pin_the_key_extremes()
    {
        var keys = KeebKeyMap.Ansi;
        var (u, v) = keys.ComputeUv();

        // ESC: wire value 0, grid (1, 3).
        Assert.Equal(0, keys.WireValues[0]);
        Assert.Equal(KeebLayout.GridToUv(1, 3).U, u[0]);
        Assert.Equal(KeebLayout.GridToUv(1, 3).V, v[0]);

        // Right arrow: wire value 121, grid (19, 8) - the bottom-right key.
        var right = keys.IndexOfWireValue(121);
        Assert.Equal(KeebLayout.GridToUv(19, 8).U, u[right]);
        Assert.Equal(KeebLayout.GridToUv(19, 8).V, v[right]);
    }

    // ── Underglow perimeter ──

    [Fact]
    public void Underglow_seed_matches_the_firmware_board_grid()
    {
        var structure = KeebZoneSupport.BuildStructure(HubId, KeebKeyMap.Ansi);
        var glow = structure.Segments[KeebZoneSupport.UnderglowSegment];
        Assert.NotNull(glow.DefaultU);
        Assert.NotNull(glow.DefaultV);
        var u = glow.DefaultU!;
        var v = glow.DefaultV!;
        Assert.Equal(KeebLayout.SurroundLedCount, u.Length);
        Assert.Equal(KeebLayout.SurroundLedCount, v.Length);
        Assert.All(u, x => Assert.InRange(x, 0f, 1f));
        Assert.All(v, x => Assert.InRange(x, 0f, 1f));

        // The ring spans the whole board, and the seam LEDs straddle top centre.
        Assert.Equal(0f, u.Min());
        Assert.Equal(1f, u.Max());
        Assert.Equal(0f, v.Min());
        Assert.Equal(1f, v.Max());
        Assert.Equal(0.5f, u[0]);   // slot 0: top centre
        Assert.Equal(0f, v[0]);
        Assert.Equal(0.55f, u[49]); // slot 49: the far side of the seam
        Assert.Equal(0f, v[49]);
    }

    [Fact]
    public void Underglow_grid_is_a_ccw_ring_seamed_at_top_centre()
    {
        var grid = KeebLayout.SurroundGrid;
        Assert.Equal(KeebLayout.SurroundLedCount, grid.Length);
        Assert.Equal(grid.Length, grid.Distinct().Count());

        // Slot 0 starts on the top edge just left of centre and the first
        // span runs LEFT to the top-left corner.
        Assert.Equal((10, 0), grid[0]);
        Assert.Equal((1, 0), grid[9]);
        // Down the left edge, then left-to-right along the bottom.
        Assert.Equal((0, 3), grid[10]);
        Assert.Equal((0, 8), grid[15]);
        Assert.Equal((1, 9), grid[16]);
        Assert.Equal((19, 9), grid[34]);
        // Up the right edge, then right-to-left back along the top to centre.
        Assert.Equal((20, 8), grid[35]);
        Assert.Equal((20, 3), grid[40]);
        Assert.Equal((19, 0), grid[41]);
        Assert.Equal((11, 0), grid[49]);
        // The last LED is the scroll wheel, which is off the perimeter.
        Assert.Equal((2, 1), grid[50]);

        // The 50 ring LEDs all sit on a board edge; the wheel does not.
        for (var i = 0; i < 50; i++)
        {
            var (column, row) = grid[i];
            Assert.True(column == 0 || column == KeebLayout.BoardGridWidth - 1
                || row == 0 || row == KeebLayout.BoardGridHeight - 1,
                $"LED {i} ({column}, {row}) is off the board edge");
        }
    }

    [Fact]
    public void Structure_authors_seeds_for_both_segments()
    {
        var structure = KeebZoneSupport.BuildStructure(HubId, KeebKeyMap.Ansi);
        var keys = structure.Segments[KeebZoneSupport.KeysSegment];
        Assert.NotNull(keys.DefaultU);
        Assert.NotNull(keys.DefaultV);
        Assert.Equal(keys.FrameLedCount, keys.DefaultU!.Length);
        Assert.Equal(keys.FrameLedCount, keys.DefaultV!.Length);
        var (expectedU, expectedV) = KeebKeyMap.Ansi.ComputeUv();
        Assert.Equal(expectedU, keys.DefaultU);
        Assert.Equal(expectedV, keys.DefaultV);
    }

    // ── Per-zone seed concatenation ──

    [Fact]
    public void Default_zone_seeds_equal_their_segment_defaults()
    {
        var structure = KeebZoneSupport.BuildStructure(HubId, KeebKeyMap.Ansi);
        var zones = ZoneResolution.Resolve(structure, new NexusSettings());

        var (keysU, keysV) = ZoneResolution.DefaultUv(structure, zones[0]);
        Assert.Equal(structure.Segments[0].DefaultU, keysU);
        Assert.Equal(structure.Segments[0].DefaultV, keysV);

        var (glowU, glowV) = ZoneResolution.DefaultUv(structure, zones[1]);
        Assert.Equal(structure.Segments[1].DefaultU, glowU);
        Assert.Equal(structure.Segments[1].DefaultV, glowV);
    }

    [Fact]
    public void Custom_partition_zone_seeds_are_slice_span_concatenations()
    {
        var structure = KeebZoneSupport.BuildStructure(HubId, KeebKeyMap.Ansi);
        var settings = new NexusSettings();
        settings.Devices.ZonePartitions[HubId] = new List<ZoneDef>
        {
            new() { Name = "Left", Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = 40 } } },
            new()
            {
                Name = "Span",
                Slices =
                {
                    new ZoneSlice { Segment = 0, Start = 40, Count = KeebKeyMap.Ansi.LedCount - 40 },
                    new ZoneSlice { Segment = 1, Start = 0, Count = 5 },
                },
            },
            new()
            {
                Name = "Tail",
                Slices = { new ZoneSlice { Segment = 1, Start = 5, Count = KeebLayout.SurroundLedCount - 5 } },
            },
        };
        var zones = ZoneResolution.Resolve(structure, settings);
        Assert.False(zones[0].IsDefault);
        var keysSeg = structure.Segments[0];
        var glowSeg = structure.Segments[1];

        // A zone sliced mid-keyboard keeps that sub-shape of the matrix.
        var (leftU, leftV) = ZoneResolution.DefaultUv(structure, zones[0]);
        Assert.Equal(40, leftU!.Length);
        Assert.Equal(keysSeg.DefaultU!.Take(40), leftU);
        Assert.Equal(keysSeg.DefaultV!.Take(40), leftV!);

        // A spanning zone concatenates its slice spans in zone-local order.
        var (spanU, spanV) = ZoneResolution.DefaultUv(structure, zones[1]);
        var keyTail = KeebKeyMap.Ansi.LedCount - 40;
        Assert.Equal(keyTail + 5, spanU!.Length);
        Assert.Equal(keysSeg.DefaultU![40], spanU[0]);
        Assert.Equal(keysSeg.DefaultU![KeebKeyMap.Ansi.LedCount - 1], spanU[keyTail - 1]);
        Assert.Equal(glowSeg.DefaultU![0], spanU[keyTail]);
        Assert.Equal(glowSeg.DefaultV![4], spanV![keyTail + 4]);

        var (tailU, _) = ZoneResolution.DefaultUv(structure, zones[2]);
        Assert.Equal(glowSeg.DefaultU!.Skip(5), tailU!);
    }

    [Fact]
    public void Zones_over_seedless_segments_yield_no_seed()
    {
        var structure = new DeviceStructure { DeviceId = "dev-1", Name = "Dev" };
        structure.Segments.Add(new StructureSegment { Index = 0, Name = "A", LedCount = 4, FrameLedCount = 4 });
        structure.DefaultZones.Add(new DefaultZoneDef
        { Id = "dev-1:a", Name = "Dev - A", Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = 4 } } });
        var zones = ZoneResolution.Resolve(structure, new NexusSettings());

        var (u, v) = ZoneResolution.DefaultUv(structure, zones[0]);
        Assert.Null(u);
        Assert.Null(v);
    }
}
