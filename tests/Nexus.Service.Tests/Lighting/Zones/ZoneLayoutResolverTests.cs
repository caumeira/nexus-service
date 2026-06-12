using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// Per-card layout resolution over the new device-scoped override store:
/// zone-local indices resolve through the zone's slices to (segment, local)
/// regardless of partition shape, so the per-card led-map endpoints keep
/// working over any custom partition.
/// </summary>
public class ZoneLayoutResolverTests
{
    private const string DeviceId = "keeb:SER1";

    private static DeviceStructure Structure()
    {
        var s = new DeviceStructure { DeviceId = DeviceId, Name = "Keeb" };
        s.Segments.Add(new StructureSegment { Index = 0, Name = "Keys", LedCount = 10, FrameLedCount = 10 });
        s.Segments.Add(new StructureSegment { Index = 1, Name = "Glow", LedCount = 6, FrameLedCount = 6 });
        s.DefaultZones.Add(new DefaultZoneDef
        { Id = DeviceId + ":keys", Name = "Keeb - Keys", Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = 10 } } });
        s.DefaultZones.Add(new DefaultZoneDef
        { Id = DeviceId + ":underglow", Name = "Keeb - Glow", Slices = { new ZoneSlice { Segment = 1, Start = 0, Count = 6 } } });
        return s;
    }

    private static NexusSettings CustomPartition()
    {
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
        return settings;
    }

    [Fact]
    public void Overrides_resolve_through_slices_for_multi_slice_zones()
    {
        var structure = Structure();
        var settings = CustomPartition();
        settings.Devices.DeviceLedOverrides[DeviceId] = new()
        {
            // Lands in the span zone via its first slice.
            new SegmentLedOverride { Segment = 0, LedIndex = 7, U = 0.9f, V = 0.8f },
            // Lands in the span zone via its second slice.
            new SegmentLedOverride { Segment = 1, LedIndex = 1, U = 0.7f, V = 0.6f, Disabled = true },
            // Belongs to the tail zone, must not leak into the span.
            new SegmentLedOverride { Segment = 1, LedIndex = 3, U = 0.5f, V = 0.4f },
        };
        var zones = ZoneResolution.Resolve(structure, settings);
        var span = zones[1];
        var ctx = ZoneResolution.ContextOf(structure, span);

        var layout = LedLayoutResolver.ResolveSeeded(span.Id, span.LedCount, null, null, settings, ctx);

        Assert.Equal(6, layout.LedCount);
        Assert.True(layout.HasUserOverrides);
        // Segment 0 local 7 = zone-local 1; segment 1 local 1 = zone-local 5.
        Assert.Equal(0.9f, layout.U[1]);
        Assert.Equal(0.7f, layout.U[5]);
        Assert.Contains(1, layout.CustomLeds);
        Assert.Contains(5, layout.CustomLeds);
        Assert.DoesNotContain(3, layout.CustomLeds);
        Assert.NotNull(layout.Disabled);
        Assert.True(layout.Disabled![5]);
        Assert.False(layout.Disabled[1]);
    }

    [Fact]
    public void Sibling_zone_sees_only_its_own_overrides()
    {
        var structure = Structure();
        var settings = CustomPartition();
        settings.Devices.DeviceLedOverrides[DeviceId] = new()
        {
            new SegmentLedOverride { Segment = 1, LedIndex = 3, U = 0.5f, V = 0.4f },
        };
        var zones = ZoneResolution.Resolve(structure, settings);
        var tail = zones[2];
        var ctx = ZoneResolution.ContextOf(structure, tail);

        var layout = LedLayoutResolver.ResolveSeeded(tail.Id, tail.LedCount, null, null, settings, ctx);
        // Segment 1 local 3 = tail-local 1.
        Assert.Equal(0.5f, layout.U[1]);
        Assert.True(layout.HasUserOverrides);

        var head = zones[0];
        var headLayout = LedLayoutResolver.ResolveSeeded(head.Id, head.LedCount, null, null, settings,
            ZoneResolution.ContextOf(structure, head));
        Assert.False(headLayout.HasUserOverrides);
        Assert.Empty(headLayout.CustomLeds);
    }

    [Fact]
    public void Default_zone_context_matches_migrated_segment_space()
    {
        var structure = Structure();
        var settings = new NexusSettings();
        // What the migration writes for a legacy "keeb:SER1:underglow" entry.
        settings.Devices.DeviceLedOverrides[DeviceId] = new()
        {
            new SegmentLedOverride { Segment = 1, LedIndex = 2, U = 0.42f, V = 0.13f },
        };
        var zones = ZoneResolution.Resolve(structure, settings);
        var underglow = zones[1];

        var layout = LedLayoutResolver.ResolveSeeded(underglow.Id, underglow.LedCount, null, null, settings,
            ZoneResolution.ContextOf(structure, underglow));
        Assert.Equal(0.42f, layout.U[2]);
        Assert.True(layout.HasUserOverrides);
    }

    [Fact]
    public void Device_aspect_ratio_applies_to_every_zone_of_the_device()
    {
        var structure = Structure();
        var settings = CustomPartition();
        settings.Devices.DeviceAspectRatios[DeviceId] = 3.25f;
        var zones = ZoneResolution.Resolve(structure, settings);

        foreach (var zone in zones)
        {
            var layout = LedLayoutResolver.ResolveSeeded(zone.Id, zone.LedCount, null, null, settings,
                ZoneResolution.ContextOf(structure, zone));
            Assert.Equal(3.25f, layout.AspectRatio);
        }
    }

    [Fact]
    public void Custom_openrgb_zone_slices_device_defaults_and_maps_overrides()
    {
        var device = new RgbDevice
        {
            Index = 0,
            Name = "Mouse",
            Type = 6,
            LedCount = 6,
            Serial = "MS01",
            Zones = new()
            {
                new RgbZone { Name = "A", ZoneType = 1, LedCount = 4 },
                new RgbZone { Name = "B", ZoneType = 1, LedCount = 2 },
            },
        };
        var settings = new NexusSettings();
        settings.Devices.ZonePartitions["openrgb-s-MS01"] = new List<ZoneDef>
        {
            new() { Name = "Front", Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = 2 } } },
            new()
            {
                Name = "Back",
                Slices =
                {
                    new ZoneSlice { Segment = 0, Start = 2, Count = 2 },
                    new ZoneSlice { Segment = 1, Start = 0, Count = 2 },
                },
            },
        };
        settings.Devices.DeviceLedOverrides["openrgb-s-MS01"] = new()
        {
            new SegmentLedOverride { Segment = 1, LedIndex = 0, U = 0.11f, V = 0.22f },
        };

        var structure = OpenRgbZoneSupport.BuildStructure(device, settings);
        var zones = ZoneResolution.Resolve(structure, settings);
        var back = zones[1];

        var layout = LedLayoutResolver.ResolveZoneOpenRgb(device, structure, back, settings);
        Assert.Equal(back.Id, layout.Id);
        Assert.Equal(4, layout.LedCount);
        // Device-space offset of the back zone's run.
        Assert.Equal(2, layout.GlobalOffset);
        // Segment 1 local 0 = zone-local 2.
        Assert.Equal(0.11f, layout.U[2]);
        Assert.Contains(2, layout.CustomLeds);
        Assert.Equal("linear", layout.ZoneTypes[0]);
    }
}
