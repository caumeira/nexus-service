using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// ZoneResolution.CountEnabled - the single place card builders get the
/// per-card enabled LED tally from. Mirrors the resolver's disabled
/// layering: applied-mapping disabled set first, user overrides mapped
/// through the zone's slices on top, an override authoritative in both
/// directions. Null structure/zone is the non-partitionable card path.
/// </summary>
public class ZoneEnabledCountTests
{
    private const string DeviceId = "dev-1";

    private static DeviceStructure Structure()
    {
        var s = new DeviceStructure { DeviceId = DeviceId, Name = "Device" };
        s.Segments.Add(new StructureSegment { Index = 0, Name = "A", LedCount = 10, FrameLedCount = 10 });
        s.Segments.Add(new StructureSegment { Index = 1, Name = "B", LedCount = 6, FrameLedCount = 6 });
        s.DefaultZones.Add(new DefaultZoneDef
        {
            Id = "legacy-a",
            Name = "Device - A",
            LegacyZoneIndex = 0,
            Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = 10 } },
        });
        s.DefaultZones.Add(new DefaultZoneDef
        {
            Id = "legacy-b",
            Name = "Device - B",
            LegacyZoneIndex = 1,
            Slices = { new ZoneSlice { Segment = 1, Start = 0, Count = 6 } },
        });
        return s;
    }

    private static void ApplyMapping(NexusSettings settings, string cardId, int zoneIndex, params int[] disabled)
    {
        var zone = new MappingZone { ZoneIndex = zoneIndex };
        zone.Disabled.AddRange(disabled);
        settings.Devices.AppliedMappings[cardId] = new AppliedMappingRef
        {
            Name = "m",
            Artifact = new MappingArtifact { Zones = { zone } },
        };
    }

    [Fact]
    public void No_disable_data_returns_full_count()
    {
        var structure = Structure();
        var zones = ZoneResolution.Resolve(structure, new NexusSettings());
        Assert.Equal(10, ZoneResolution.CountEnabled(structure, zones[0], zones[0].Id, 10, new NexusSettings()));
        Assert.Equal(7, ZoneResolution.CountEnabled(null, null, "card-1", 7, new NexusSettings()));
        Assert.Equal(0, ZoneResolution.CountEnabled(null, null, "card-1", 0, new NexusSettings()));
    }

    [Fact]
    public void Override_disabled_leds_subtract()
    {
        var structure = Structure();
        var settings = new NexusSettings();
        settings.Devices.DeviceLedOverrides[DeviceId] = new()
        {
            new SegmentLedOverride { Segment = 0, LedIndex = 2, Disabled = true },
            new SegmentLedOverride { Segment = 0, LedIndex = 5, Disabled = true },
            // Position-only override must not subtract.
            new SegmentLedOverride { Segment = 0, LedIndex = 7, U = 0.5f, V = 0.5f },
            // Other segment: outside zone A, must not leak in.
            new SegmentLedOverride { Segment = 1, LedIndex = 0, Disabled = true },
        };
        var zones = ZoneResolution.Resolve(structure, settings);
        Assert.Equal(8, ZoneResolution.CountEnabled(structure, zones[0], zones[0].Id, 10, settings));
        Assert.Equal(5, ZoneResolution.CountEnabled(structure, zones[1], zones[1].Id, 6, settings));
    }

    [Fact]
    public void Mapping_disabled_leds_subtract()
    {
        var structure = Structure();
        var settings = new NexusSettings();
        // Out-of-range indices are ignored, matching the resolver's guard.
        ApplyMapping(settings, "legacy-a", zoneIndex: 0, 1, 4, 99, -1);
        var zones = ZoneResolution.Resolve(structure, settings);
        Assert.Equal(8, ZoneResolution.CountEnabled(structure, zones[0], zones[0].Id, 10, settings));
        // No mapping applied to the sibling card.
        Assert.Equal(6, ZoneResolution.CountEnabled(structure, zones[1], zones[1].Id, 6, settings));
    }

    [Fact]
    public void Override_reenable_beats_mapping_disable()
    {
        var structure = Structure();
        var settings = new NexusSettings();
        ApplyMapping(settings, "legacy-a", zoneIndex: 0, 1, 4);
        settings.Devices.DeviceLedOverrides[DeviceId] = new()
        {
            // Re-enables a mapping-disabled LED.
            new SegmentLedOverride { Segment = 0, LedIndex = 4, Disabled = false },
            // And disables one the mapping left on.
            new SegmentLedOverride { Segment = 0, LedIndex = 8, Disabled = true },
        };
        var zones = ZoneResolution.Resolve(structure, settings);
        // Mapping disables 1 and 4; override re-enables 4 and disables 8.
        Assert.Equal(8, ZoneResolution.CountEnabled(structure, zones[0], zones[0].Id, 10, settings));
    }

    [Fact]
    public void Multi_slice_zone_counts_across_segments()
    {
        var structure = Structure();
        var settings = new NexusSettings();
        settings.Devices.ZonePartitions[DeviceId] = new List<ZoneDef>
        {
            new() { Name = "Head", Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = 6 } } },
            new()
            {
                Name = "Span",
                Slices =
                {
                    new ZoneSlice { Segment = 0, Start = 6, Count = 4 },
                    new ZoneSlice { Segment = 1, Start = 0, Count = 2 },
                },
            },
            new() { Name = "Tail", Slices = { new ZoneSlice { Segment = 1, Start = 2, Count = 4 } } },
        };
        settings.Devices.DeviceLedOverrides[DeviceId] = new()
        {
            // Span zone via its first slice (segment 0 local 7 = zone-local 1).
            new SegmentLedOverride { Segment = 0, LedIndex = 7, Disabled = true },
            // Span zone via its second slice (segment 1 local 1 = zone-local 5).
            new SegmentLedOverride { Segment = 1, LedIndex = 1, Disabled = true },
            // Head and tail zones, one each.
            new SegmentLedOverride { Segment = 0, LedIndex = 0, Disabled = true },
            new SegmentLedOverride { Segment = 1, LedIndex = 5, Disabled = true },
        };
        var zones = ZoneResolution.Resolve(structure, settings);
        // Head: one of six disabled. Span: one per slice across both
        // segments. Tail: one of four disabled.
        Assert.Equal(5, ZoneResolution.CountEnabled(structure, zones[0], zones[0].Id, zones[0].LedCount, settings));
        Assert.Equal(4, ZoneResolution.CountEnabled(structure, zones[1], zones[1].Id, zones[1].LedCount, settings));
        Assert.Equal(3, ZoneResolution.CountEnabled(structure, zones[2], zones[2].Id, zones[2].LedCount, settings));
    }

    [Fact]
    public void Non_partitionable_card_uses_identity_context()
    {
        var settings = new NexusSettings();
        ApplyMapping(settings, "np50:1:p1", zoneIndex: 0, 0);
        settings.Devices.DeviceLedOverrides["np50:1:p1"] = new()
        {
            // Identity context: segment 0 is the card's own LED space.
            new SegmentLedOverride { Segment = 0, LedIndex = 3, Disabled = true },
            // Non-zero segments never map for identity cards.
            new SegmentLedOverride { Segment = 1, LedIndex = 1, Disabled = true },
        };
        // Mapping disables LED 0, override disables LED 3.
        Assert.Equal(8, ZoneResolution.CountEnabled(null, null, "np50:1:p1", 10, settings));
        // Disable data for one card never bleeds into another.
        Assert.Equal(10, ZoneResolution.CountEnabled(null, null, "np50:1:p2", 10, settings));
    }
}
