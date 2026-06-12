using Nexus.Service.Lighting.Zones;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// Index-stability invariant, rule by rule: exact tiling (rule 1), resizable
/// walls (rule 2), spanning-zone contiguity across fixed segments (rule 3),
/// plus zone-name hygiene.
/// </summary>
public class ZonePartitionValidatorTests
{
    private static List<StructureSegment> TwoFixedSegments() => new()
    {
        new StructureSegment { Index = 0, Name = "A", LedCount = 10, FrameLedCount = 10, Resizable = false, ZoneType = "linear" },
        new StructureSegment { Index = 1, Name = "B", LedCount = 6, FrameLedCount = 6, Resizable = false, ZoneType = "linear" },
    };

    private static List<StructureSegment> FixedPlusResizable() => new()
    {
        new StructureSegment { Index = 0, Name = "Fixed", LedCount = 10, FrameLedCount = 10, Resizable = false, ZoneType = "linear" },
        new StructureSegment { Index = 1, Name = "Header", LedCount = 60, FrameLedCount = 60, Resizable = true, ZoneType = "linear" },
    };

    private static ZoneDef Zone(string name, params (int seg, int start, int count)[] slices)
    {
        var zone = new ZoneDef { Name = name };
        foreach (var (seg, start, count) in slices)
            zone.Slices.Add(new ZoneSlice { Segment = seg, Start = start, Count = count });
        return zone;
    }

    // ── happy paths ──────────────────────────────────────────────────────

    [Fact]
    public void One_zone_per_segment_passes()
    {
        var result = ZonePartitionValidator.Validate(TwoFixedSegments(), new List<ZoneDef>
        {
            Zone("Top", (0, 0, 10)),
            Zone("Bottom", (1, 0, 6)),
        });
        Assert.True(result.Ok);
    }

    [Fact]
    public void Split_within_a_fixed_segment_passes()
    {
        var result = ZonePartitionValidator.Validate(TwoFixedSegments(), new List<ZoneDef>
        {
            Zone("Left", (0, 0, 4)),
            Zone("Right", (0, 4, 6)),
            Zone("Bottom", (1, 0, 6)),
        });
        Assert.True(result.Ok);
    }

    [Fact]
    public void Spanning_zone_across_fixed_segments_passes_when_contiguous()
    {
        var result = ZonePartitionValidator.Validate(TwoFixedSegments(), new List<ZoneDef>
        {
            Zone("Head", (0, 0, 4)),
            Zone("Wrap", (0, 4, 6), (1, 0, 2)),
            Zone("Tail", (1, 2, 4)),
        });
        Assert.True(result.Ok);
    }

    [Fact]
    public void Whole_resizable_segment_zone_passes()
    {
        var result = ZonePartitionValidator.Validate(FixedPlusResizable(), new List<ZoneDef>
        {
            Zone("Fixed", (0, 0, 10)),
            Zone("Header", (1, 0, 60)),
        });
        Assert.True(result.Ok);
    }

    // ── rule 1: exact tiling ─────────────────────────────────────────────

    [Fact]
    public void Gap_in_a_segment_fails()
    {
        var result = ZonePartitionValidator.Validate(TwoFixedSegments(), new List<ZoneDef>
        {
            Zone("Left", (0, 0, 4)),
            Zone("Right", (0, 5, 5)),
            Zone("Bottom", (1, 0, 6)),
        });
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("tile"));
    }

    [Fact]
    public void Overlap_in_a_segment_fails()
    {
        var result = ZonePartitionValidator.Validate(TwoFixedSegments(), new List<ZoneDef>
        {
            Zone("Left", (0, 0, 6)),
            Zone("Right", (0, 4, 6)),
            Zone("Bottom", (1, 0, 6)),
        });
        Assert.False(result.Ok);
    }

    [Fact]
    public void Uncovered_segment_fails()
    {
        var result = ZonePartitionValidator.Validate(TwoFixedSegments(), new List<ZoneDef>
        {
            Zone("Top", (0, 0, 10)),
        });
        Assert.False(result.Ok);
    }

    [Fact]
    public void Slice_beyond_segment_bounds_fails()
    {
        var result = ZonePartitionValidator.Validate(TwoFixedSegments(), new List<ZoneDef>
        {
            Zone("Top", (0, 0, 11)),
            Zone("Bottom", (1, 0, 6)),
        });
        Assert.False(result.Ok);
    }

    [Fact]
    public void Unknown_segment_index_fails()
    {
        var result = ZonePartitionValidator.Validate(TwoFixedSegments(), new List<ZoneDef>
        {
            Zone("Top", (0, 0, 10)),
            Zone("Bottom", (1, 0, 6)),
            Zone("Ghost", (2, 0, 3)),
        });
        Assert.False(result.Ok);
    }

    [Fact]
    public void Empty_partition_fails()
    {
        var result = ZonePartitionValidator.Validate(TwoFixedSegments(), new List<ZoneDef>());
        Assert.False(result.Ok);
    }

    // ── rule 2: resizable walls ──────────────────────────────────────────

    [Fact]
    public void Partial_slice_on_resizable_segment_fails()
    {
        var result = ZonePartitionValidator.Validate(FixedPlusResizable(), new List<ZoneDef>
        {
            Zone("Fixed", (0, 0, 10)),
            Zone("HeaderA", (1, 0, 30)),
            Zone("HeaderB", (1, 30, 30)),
        });
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("resizable"));
    }

    [Fact]
    public void Zone_spanning_fixed_into_resizable_fails()
    {
        var result = ZonePartitionValidator.Validate(FixedPlusResizable(), new List<ZoneDef>
        {
            Zone("Head", (0, 0, 5)),
            Zone("Wall", (0, 5, 5), (1, 0, 60)),
        });
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("resizable"));
    }

    // ── rule 3: spanning-zone contiguity ─────────────────────────────────

    [Fact]
    public void Non_contiguous_slices_fail()
    {
        var segments = new List<StructureSegment>
        {
            new() { Index = 0, Name = "A", LedCount = 10, FrameLedCount = 10 },
            new() { Index = 1, Name = "B", LedCount = 6, FrameLedCount = 6 },
            new() { Index = 2, Name = "C", LedCount = 4, FrameLedCount = 4 },
        };
        var result = ZonePartitionValidator.Validate(segments, new List<ZoneDef>
        {
            // Skips segment B entirely - not a contiguous device run.
            Zone("Hole", (0, 0, 10), (2, 0, 4)),
            Zone("Middle", (1, 0, 6)),
        });
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("contiguous"));
    }

    [Fact]
    public void Reversed_slice_order_fails()
    {
        var result = ZonePartitionValidator.Validate(TwoFixedSegments(), new List<ZoneDef>
        {
            Zone("Backwards", (1, 0, 6), (0, 0, 10)),
        });
        Assert.False(result.Ok);
    }

    // ── names ────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_or_whitespace_name_fails()
    {
        var result = ZonePartitionValidator.Validate(TwoFixedSegments(), new List<ZoneDef>
        {
            Zone("   ", (0, 0, 10)),
            Zone("Bottom", (1, 0, 6)),
        });
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("name"));
    }

    [Fact]
    public void Name_over_cap_fails()
    {
        var result = ZonePartitionValidator.Validate(TwoFixedSegments(), new List<ZoneDef>
        {
            Zone(new string('x', ZonePartitionValidator.MaxZoneNameLength + 1), (0, 0, 10)),
            Zone("Bottom", (1, 0, 6)),
        });
        Assert.False(result.Ok);
    }

    [Fact]
    public void Name_at_cap_passes()
    {
        var result = ZonePartitionValidator.Validate(TwoFixedSegments(), new List<ZoneDef>
        {
            Zone(new string('x', ZonePartitionValidator.MaxZoneNameLength), (0, 0, 10)),
            Zone("Bottom", (1, 0, 6)),
        });
        Assert.True(result.Ok);
    }

    [Fact]
    public void Zone_without_slices_fails()
    {
        var result = ZonePartitionValidator.Validate(TwoFixedSegments(), new List<ZoneDef>
        {
            Zone("Top", (0, 0, 10)),
            Zone("Bottom", (1, 0, 6)),
            new ZoneDef { Name = "Empty" },
        });
        Assert.False(result.Ok);
    }

    [Fact]
    public void Zero_length_slice_on_fixed_segment_fails()
    {
        var result = ZonePartitionValidator.Validate(TwoFixedSegments(), new List<ZoneDef>
        {
            Zone("Top", (0, 0, 10)),
            Zone("Bottom", (1, 0, 6)),
            Zone("Nothing", (1, 3, 0)),
        });
        Assert.False(result.Ok);
    }
}
