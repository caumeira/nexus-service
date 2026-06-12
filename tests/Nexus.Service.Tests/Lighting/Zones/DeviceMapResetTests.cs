using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// Factory-defaults paths for the device-scoped LED map: the defaults facade
/// keeps the hardware/canvas shape (partition, wired LED counts, aspect
/// ratio) while dropping the user layers (overrides, applied mappings); the
/// DELETE mutation removes exactly the device's override and aspect-ratio
/// entries.
/// </summary>
public class DeviceMapResetTests
{
    private const string DeviceId = "keeb:SER1";
    private const string ZoneId = "keeb:SER1:keys";
    private const string Other = "openrgb-s-X-0";

    private static NexusSettings Seeded()
    {
        var settings = new NexusSettings();
        settings.Devices.ZonePartitions[DeviceId] = new List<ZoneDef>
        {
            new() { Name = "Keys", Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = 4 } } },
        };
        settings.Devices.ZoneLedCounts[Other] = 60;
        settings.Devices.DeviceLedOverrides[DeviceId] = new()
        {
            new SegmentLedOverride { Segment = 0, LedIndex = 1, U = 0.5f, V = 0.5f },
        };
        settings.Devices.DeviceAspectRatios[DeviceId] = 2f;
        settings.Devices.AppliedMappings[ZoneId] = new AppliedMappingRef { Name = "m" };
        return settings;
    }

    [Fact]
    public void Defaults_facade_keeps_partition_counts_and_ratio_drops_user_layers()
    {
        var settings = Seeded();
        var facade = DevicesRoutes.DeviceMapDefaultsFacade(settings);

        Assert.Same(settings.Devices.ZonePartitions, facade.Devices.ZonePartitions);
        Assert.Same(settings.Devices.ZoneLedCounts, facade.Devices.ZoneLedCounts);
        // The aspect ratio shapes the editor canvas rather than the LED
        // layout, so the defaults preview keeps it (same as the per-card
        // led-map defaults facade).
        Assert.Same(settings.Devices.DeviceAspectRatios, facade.Devices.DeviceAspectRatios);

        Assert.Empty(facade.Devices.DeviceLedOverrides);
        Assert.Empty(facade.Devices.AppliedMappings);

        // The facade is a read-only view; the persisted snapshot keeps its
        // user layers.
        Assert.Single(settings.Devices.DeviceLedOverrides[DeviceId]);
        Assert.Equal(2f, settings.Devices.DeviceAspectRatios[DeviceId]);
        Assert.True(settings.Devices.AppliedMappings.ContainsKey(ZoneId));
    }

    [Fact]
    public void Clear_removes_exactly_the_devices_override_and_ratio_entries()
    {
        var settings = Seeded();
        // Sibling state that must survive the reset.
        settings.Devices.DeviceLedOverrides[Other] = new()
        {
            new SegmentLedOverride { Segment = 0, LedIndex = 0, U = 0.1f, V = 0.1f },
        };
        settings.Devices.DeviceAspectRatios[Other] = 1.5f;
        settings.Devices.LightingDevicePrefs[ZoneId] = new LightingDevicePreference { Brightness = 33 };
        settings.Lighting.DeviceLayouts[ZoneId] = new DeviceLayout { X = 1, Y = 2 };

        DevicesRoutes.ClearDeviceMapState(settings, DeviceId);

        Assert.False(settings.Devices.DeviceLedOverrides.ContainsKey(DeviceId));
        Assert.False(settings.Devices.DeviceAspectRatios.ContainsKey(DeviceId));

        // Partition, prefs, layouts, applied mappings, and the other
        // device's entries are untouched.
        Assert.True(settings.Devices.ZonePartitions.ContainsKey(DeviceId));
        Assert.True(settings.Devices.AppliedMappings.ContainsKey(ZoneId));
        Assert.True(settings.Devices.LightingDevicePrefs.ContainsKey(ZoneId));
        Assert.True(settings.Lighting.DeviceLayouts.ContainsKey(ZoneId));
        Assert.Single(settings.Devices.DeviceLedOverrides[Other]);
        Assert.Equal(1.5f, settings.Devices.DeviceAspectRatios[Other]);
        Assert.Equal(60, settings.Devices.ZoneLedCounts[Other]);
    }

    [Fact]
    public void Clear_is_a_no_op_when_the_device_has_no_entries()
    {
        var settings = new NexusSettings();
        DevicesRoutes.ClearDeviceMapState(settings, DeviceId);
        Assert.Empty(settings.Devices.DeviceLedOverrides);
        Assert.Empty(settings.Devices.DeviceAspectRatios);
    }
}
