using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// <see cref="OpenRgbZoneSupport.IsFullyUndriven"/> gates whether RgbBridge
/// claims direct mode / pushes frames for a physical device: true only when
/// every card the device would emit is in the undriven set.
/// </summary>
public class OpenRgbZoneUndrivenTests
{
    private static RgbDevice Motherboard() => new()
    {
        Index = 0,
        Name = "B850I AORUS PRO",
        Type = 0,
        LedCount = 3,
        Serial = "MB01",
        Zones = new()
        {
            new RgbZone { Name = "D_LED1", ZoneType = 1, LedCount = 1 },
            new RgbZone { Name = "D_LED2", ZoneType = 1, LedCount = 1 },
            new RgbZone { Name = "LED_C1C2", ZoneType = 0, LedCount = 1 },
        },
    };

    private static RgbDevice Mouse() => new()
    {
        Index = 1,
        Name = "Gaming Mouse",
        Type = 6,
        LedCount = 4,
        Serial = "MS01",
        Zones = new()
        {
            new RgbZone { Name = "Logo", ZoneType = 1, LedCount = 1 },
            new RgbZone { Name = "Wheel", ZoneType = 1, LedCount = 3 },
        },
    };

    [Fact]
    public void Empty_undriven_list_is_never_fully_undriven()
    {
        Assert.False(OpenRgbZoneSupport.IsFullyUndriven(Mouse(), new NexusSettings()));
    }

    [Fact]
    public void Whole_device_card_undriven_marks_device_fully_undriven()
    {
        var settings = new NexusSettings();
        settings.Devices.UndrivenLightingDevices.Add("openrgb-s-MS01");

        Assert.True(OpenRgbZoneSupport.IsFullyUndriven(Mouse(), settings));
    }

    [Fact]
    public void Other_ids_undriven_does_not_affect_unrelated_device()
    {
        var settings = new NexusSettings();
        settings.Devices.UndrivenLightingDevices.Add("openrgb-s-OTHER");

        Assert.False(OpenRgbZoneSupport.IsFullyUndriven(Mouse(), settings));
    }

    [Fact]
    public void Split_motherboard_requires_every_zone_undriven()
    {
        var settings = new NexusSettings();
        settings.Devices.UndrivenLightingDevices.Add("openrgb-s-MB01-0");
        settings.Devices.UndrivenLightingDevices.Add("openrgb-s-MB01-1");
        // openrgb-s-MB01-2 stays driven.

        Assert.False(OpenRgbZoneSupport.IsFullyUndriven(Motherboard(), settings));
    }

    [Fact]
    public void Split_motherboard_fully_undriven_when_all_zones_listed()
    {
        var settings = new NexusSettings();
        settings.Devices.UndrivenLightingDevices.Add("openrgb-s-MB01-0");
        settings.Devices.UndrivenLightingDevices.Add("openrgb-s-MB01-1");
        settings.Devices.UndrivenLightingDevices.Add("openrgb-s-MB01-2");

        Assert.True(OpenRgbZoneSupport.IsFullyUndriven(Motherboard(), settings));
    }
}
