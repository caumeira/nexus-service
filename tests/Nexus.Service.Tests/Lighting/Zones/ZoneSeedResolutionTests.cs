using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// Segment-default seeds flowing through the resolution surfaces: the zone
/// topology's card resolution (which backs the led-map and device-map GETs
/// and the engine refresh), the device-map defaults facade, and the
/// contributor frame tracker. Keeb zones must come out shaped like the
/// keyboard, with user overrides layering on top in segment-local space.
/// </summary>
public class ZoneSeedResolutionTests : IDisposable
{
    private const string HubId = "keeb:SER123";

    private readonly TempDir _dir = new();
    private readonly LightingEngine _engine = new();

    private sealed class FakeStructureSource : IDeviceStructureSource
    {
        public IReadOnlyList<DeviceStructure> GetStructures() => new[] { KeebZoneSupport.BuildStructure(HubId) };
    }

    private ZoneTopology Topology(ContributorFrameLayouts? tracker = null) => new(
        new IDeviceStructureSource[] { new FakeStructureSource() },
        new TestableConfigStore(_dir.At("settings.json")),
        _engine,
        tracker ?? new ContributorFrameLayouts());

    public void Dispose()
    {
        _engine.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Default_keys_zone_resolves_to_the_key_matrix_seed()
    {
        var resolution = Topology().ResolveCard(HubId + ":keys", new NexusSettings());

        Assert.NotNull(resolution);
        var (seedU, seedV) = KeebLayout.ComputeKeyUv();
        Assert.Equal(seedU, resolution!.Layout.U);
        Assert.Equal(seedV, resolution.Layout.V);
        Assert.Null(resolution.Device);
    }

    [Fact]
    public void Overrides_layer_on_top_of_the_seed_in_segment_space()
    {
        var settings = new NexusSettings();
        settings.Devices.DeviceLedOverrides[HubId] = new()
        {
            new SegmentLedOverride { Segment = 0, LedIndex = 1, U = 0.9f, V = 0.8f },
        };

        var layout = Topology().ResolveCard(HubId + ":keys", settings)!.Layout;

        var (seedU, _) = KeebLayout.ComputeKeyUv();
        Assert.Equal(0.9f, layout.U[1]);
        Assert.Contains(1, layout.CustomLeds);
        Assert.True(layout.HasUserOverrides);
        // Every other LED keeps the stock matrix position.
        Assert.Equal(seedU[0], layout.U[0]);
        Assert.Equal(seedU[2], layout.U[2]);
    }

    [Fact]
    public void Defaults_facade_returns_pure_seeds_with_user_layers_dropped()
    {
        var settings = new NexusSettings();
        settings.Devices.DeviceLedOverrides[HubId] = new()
        {
            new SegmentLedOverride { Segment = 0, LedIndex = 0, U = 0.9f, V = 0.9f },
        };

        var facade = DevicesRoutes.DeviceMapDefaultsFacade(settings);
        var layout = Topology().ResolveCard(HubId + ":keys", facade)!.Layout;

        var (seedU, seedV) = KeebLayout.ComputeKeyUv();
        Assert.Equal(seedU, layout.U);
        Assert.Equal(seedV, layout.V);
        Assert.False(layout.HasUserOverrides);
        Assert.Empty(layout.CustomLeds);
    }

    [Fact]
    public void Custom_partition_zone_resolves_to_its_sliced_sub_shape()
    {
        var settings = new NexusSettings();
        settings.Devices.ZonePartitions[HubId] = new List<ZoneDef>
        {
            new() { Name = "Left", Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = 40 } } },
            new()
            {
                Name = "Rest",
                Slices =
                {
                    new ZoneSlice { Segment = 0, Start = 40, Count = KeebLayout.KeyLedCount - 40 },
                    new ZoneSlice { Segment = 1, Start = 0, Count = KeebLayout.SurroundLedCount },
                },
            },
        };

        var layout = Topology().ResolveCard($"{HubId}:z1", settings)!.Layout;

        var structure = KeebZoneSupport.BuildStructure(HubId);
        var keyTail = KeebLayout.KeyLedCount - 40;
        Assert.Equal(keyTail + KeebLayout.SurroundLedCount, layout.LedCount);
        Assert.Equal(structure.Segments[0].DefaultU![40], layout.U[0]);
        Assert.Equal(structure.Segments[1].DefaultU![0], layout.U[keyTail]);
        Assert.Equal(structure.Segments[1].DefaultV![0], layout.V[keyTail]);
    }

    [Fact]
    public void Engine_refresh_pushes_seeded_layout_through_the_tracker()
    {
        var tracker = new ContributorFrameLayouts();
        var topology = Topology(tracker);
        var frame = new DeviceFrame(0, HubId + ":keys", KeebLayout.KeyLedCount);
        _engine.UpdateDevices(new[] { frame });

        topology.RefreshCardFrame(HubId + ":keys");

        var (seedU, seedV) = KeebLayout.ComputeKeyUv();
        Assert.Equal(seedU, frame.LedU);
        Assert.Equal(seedV, frame.LedV);

        // A bridge rebuild reusing the same frame instance (structure seeds
        // passed, as RgbBridge does for zone-backed contributor frames) must
        // keep the stock shape rather than collapse to a line.
        var structure = KeebZoneSupport.BuildStructure(HubId);
        var zones = ZoneResolution.Resolve(structure, new NexusSettings());
        var (rebuildU, rebuildV) = ZoneResolution.DefaultUv(structure, zones[0]);
        tracker.Refresh(frame, new NexusSettings(), ZoneResolution.ContextOf(structure, zones[0]), rebuildU, rebuildV);
        Assert.Equal(seedU, frame.LedU);
        Assert.Equal(seedV, frame.LedV);
    }
}
