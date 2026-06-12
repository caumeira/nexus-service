using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// Stock per-LED positions for the keeb: key UVs derived from the firmware
/// wire values (row-major matrix decode), underglow as a clockwise unit
/// square perimeter walk, and per-zone seeds as the concatenation of each
/// zone's slice spans over the segment defaults - for the default partition
/// and any custom partition shape alike.
/// </summary>
public class KeebStockUvTests
{
    private const string HubId = "keeb:SER123";

    // ── Key matrix derivation ──

    [Fact]
    public void Key_uv_count_matches_physical_key_count()
    {
        var (u, v) = KeebLayout.ComputeKeyUv();
        Assert.Equal(KeebLayout.KeyLedCount, u.Length);
        Assert.Equal(KeebLayout.KeyLedCount, v.Length);
    }

    [Fact]
    public void First_key_lands_on_the_top_left_corner()
    {
        // Firmware value 0 decodes to row 0, column 0.
        var (u, v) = KeebLayout.ComputeKeyUv();
        Assert.Equal(0, KeebLayout.KeyWireValues[0]);
        Assert.Equal(0f, u[0]);
        Assert.Equal(0f, v[0]);
    }

    [Fact]
    public void Last_wire_value_lands_on_the_bottom_right_corner()
    {
        var (u, v) = KeebLayout.ComputeKeyUv();
        var idx = Array.IndexOf(KeebLayout.KeyWireValues, 121);
        Assert.True(idx >= 0);
        Assert.Equal(1f, u[idx]);
        Assert.Equal(1f, v[idx]);
    }

    [Fact]
    public void Row_end_and_row_start_keys_pin_the_horizontal_extremes()
    {
        var (u, v) = KeebLayout.ComputeKeyUv();
        // Last key of the top firmware row: rightmost column, top row.
        var topRight = Array.IndexOf(KeebLayout.KeyWireValues, 16);
        Assert.True(topRight >= 0);
        Assert.Equal(1f, u[topRight]);
        Assert.Equal(0f, v[topRight]);
        // First key of the bottom firmware row: leftmost column, bottom row.
        var bottomLeft = Array.IndexOf(KeebLayout.KeyWireValues, KeebLayout.KeyMatrixStride * 5);
        Assert.True(bottomLeft >= 0);
        Assert.Equal(0f, u[bottomLeft]);
        Assert.Equal(1f, v[bottomLeft]);
    }

    [Fact]
    public void Every_wire_value_decodes_inside_the_six_row_matrix()
    {
        // Each firmware value must decode into the occupied row/column range
        // of the matrix, so the derived positions never leave the unit square.
        var (u, v) = KeebLayout.ComputeKeyUv();
        var rows = new HashSet<int>();
        for (var i = 0; i < KeebLayout.KeyWireValues.Length; i++)
        {
            var value = KeebLayout.KeyWireValues[i];
            var row = value / KeebLayout.KeyMatrixStride;
            var col = value % KeebLayout.KeyMatrixStride;
            Assert.InRange(row, 0, 5);
            Assert.InRange(col, 0, 16);
            rows.Add(row);
            Assert.InRange(u[i], 0f, 1f);
            Assert.InRange(v[i], 0f, 1f);
        }
        Assert.Equal(6, rows.Count);
    }

    // ── Underglow perimeter ──

    [Fact]
    public void Underglow_seed_walks_the_full_perimeter_clockwise()
    {
        var structure = KeebZoneSupport.BuildStructure(HubId);
        var glow = structure.Segments[KeebZoneSupport.UnderglowSegment];
        Assert.NotNull(glow.DefaultU);
        Assert.NotNull(glow.DefaultV);
        var u = glow.DefaultU!;
        var v = glow.DefaultV!;
        Assert.Equal(KeebLayout.SurroundLedCount, u.Length);
        Assert.Equal(KeebLayout.SurroundLedCount, v.Length);

        // Every LED sits exactly on a unit-square edge, inside [0,1].
        for (var i = 0; i < u.Length; i++)
        {
            Assert.InRange(u[i], 0f, 1f);
            Assert.InRange(v[i], 0f, 1f);
            Assert.True(u[i] == 0f || u[i] == 1f || v[i] == 0f || v[i] == 1f,
                $"LED {i} ({u[i]}, {v[i]}) is off the perimeter");
        }

        // Walk starts at the top-left corner and reaches the opposite corner.
        Assert.Equal(0f, u[0]);
        Assert.Equal(0f, v[0]);
        Assert.Contains(Enumerable.Range(0, u.Length), i => u[i] == 1f && v[i] == 1f);

        // Clockwise: the second LED moves along the top edge.
        Assert.True(u[1] > 0f && v[1] == 0f);

        // All four edges carry LEDs strictly between corners.
        Assert.Contains(Enumerable.Range(0, u.Length), i => v[i] == 0f && u[i] > 0f && u[i] < 1f);
        Assert.Contains(Enumerable.Range(0, u.Length), i => u[i] == 1f && v[i] > 0f && v[i] < 1f);
        Assert.Contains(Enumerable.Range(0, u.Length), i => v[i] == 1f && u[i] > 0f && u[i] < 1f);
        Assert.Contains(Enumerable.Range(0, u.Length), i => u[i] == 0f && v[i] > 0f && v[i] < 1f);
    }

    [Fact]
    public void Structure_authors_seeds_for_both_segments()
    {
        var structure = KeebZoneSupport.BuildStructure(HubId);
        var keys = structure.Segments[KeebZoneSupport.KeysSegment];
        Assert.NotNull(keys.DefaultU);
        Assert.NotNull(keys.DefaultV);
        Assert.Equal(keys.FrameLedCount, keys.DefaultU!.Length);
        Assert.Equal(keys.FrameLedCount, keys.DefaultV!.Length);
        var (expectedU, expectedV) = KeebLayout.ComputeKeyUv();
        Assert.Equal(expectedU, keys.DefaultU);
        Assert.Equal(expectedV, keys.DefaultV);
    }

    // ── Per-zone seed concatenation ──

    [Fact]
    public void Default_zone_seeds_equal_their_segment_defaults()
    {
        var structure = KeebZoneSupport.BuildStructure(HubId);
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
        var structure = KeebZoneSupport.BuildStructure(HubId);
        var settings = new NexusSettings();
        settings.Devices.ZonePartitions[HubId] = new List<ZoneDef>
        {
            new() { Name = "Left", Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = 40 } } },
            new()
            {
                Name = "Span",
                Slices =
                {
                    new ZoneSlice { Segment = 0, Start = 40, Count = KeebLayout.KeyLedCount - 40 },
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
        var keyTail = KeebLayout.KeyLedCount - 40;
        Assert.Equal(keyTail + 5, spanU!.Length);
        Assert.Equal(keysSeg.DefaultU![40], spanU[0]);
        Assert.Equal(keysSeg.DefaultU![KeebLayout.KeyLedCount - 1], spanU[keyTail - 1]);
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
