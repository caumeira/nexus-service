using Nexus.Service.Lighting.Mappings;

namespace Nexus.Service.Tests.Lighting.Mappings;

public class MappingLintTests
{
    private static MappingArtifact Valid(int ledCount = 12)
    {
        var zone = new MappingZone { ZoneIndex = 0, LedCount = ledCount };
        for (int i = 0; i < ledCount; i++)
        {
            // Ring layout: clearly 2D, never colinear.
            var angle = 2 * MathF.PI * i / ledCount;
            zone.Leds.Add(new MappingLed
            {
                I = i,
                U = 0.5f + 0.4f * MathF.Cos(angle),
                V = 0.5f + 0.4f * MathF.Sin(angle),
            });
        }
        return new MappingArtifact
        {
            Name = "test",
            Device = new MappingDeviceInfo { Key = "usb:1b1c:0c1a" },
            Zones = new() { zone },
        };
    }

    [Fact]
    public void Valid_artifact_passes()
    {
        var result = MappingLint.Validate(Valid());
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.False(result.AutoApplyIneligible);
    }

    [Fact]
    public void Null_artifact_fails()
        => Assert.False(MappingLint.Validate(null).Ok);

    [Fact]
    public void Wrong_schema_version_fails()
    {
        var artifact = Valid();
        artifact.SchemaVersion = 99;
        Assert.False(MappingLint.Validate(artifact).Ok);
    }

    [Fact]
    public void Missing_device_key_fails()
    {
        var artifact = Valid();
        artifact.Device.Key = "";
        Assert.False(MappingLint.Validate(artifact).Ok);
    }

    [Fact]
    public void Uv_out_of_range_fails()
    {
        var artifact = Valid();
        artifact.Zones[0].Leds[0].U = 1.5f;
        Assert.False(MappingLint.Validate(artifact).Ok);
    }

    [Fact]
    public void Nan_uv_fails()
    {
        var artifact = Valid();
        artifact.Zones[0].Leds[0].V = float.NaN;
        Assert.False(MappingLint.Validate(artifact).Ok);
    }

    [Fact]
    public void Duplicate_led_index_fails()
    {
        var artifact = Valid();
        artifact.Zones[0].Leds[1].I = 0;
        Assert.False(MappingLint.Validate(artifact).Ok);
    }

    [Fact]
    public void Led_index_beyond_count_fails()
    {
        var artifact = Valid();
        artifact.Zones[0].Leds[0].I = 99;
        Assert.False(MappingLint.Validate(artifact).Ok);
    }

    [Fact]
    public void Empty_zones_fails()
        => Assert.False(MappingLint.Validate(new MappingArtifact
        {
            Device = new MappingDeviceInfo { Key = "usb:0000:0000" },
        }).Ok);

    [Fact]
    public void All_positions_identical_fails()
    {
        var artifact = Valid();
        foreach (var led in artifact.Zones[0].Leds)
        { led.U = 0.5f; led.V = 0.5f; }
        Assert.False(MappingLint.Validate(artifact).Ok);
    }

    [Fact]
    public void Colinear_rejected_for_matrix_zone()
    {
        var artifact = Valid();
        for (int i = 0; i < artifact.Zones[0].Leds.Count; i++)
        {
            artifact.Zones[0].Leds[i].U = i / 11f;
            artifact.Zones[0].Leds[i].V = 0.5f;
        }
        var result = MappingLint.Validate(artifact, zoneIsLinear: _ => false);
        Assert.False(result.Ok);
    }

    [Fact]
    public void Colinear_allowed_for_linear_zone()
    {
        var artifact = Valid();
        for (int i = 0; i < artifact.Zones[0].Leds.Count; i++)
        {
            artifact.Zones[0].Leds[i].U = i / 11f;
            artifact.Zones[0].Leds[i].V = 0.5f;
        }
        Assert.True(MappingLint.Validate(artifact, zoneIsLinear: _ => true).Ok);
        Assert.True(MappingLint.Validate(artifact).Ok);
    }

    [Fact]
    public void Diagonal_colinear_rejected_for_matrix_zone()
    {
        var artifact = Valid();
        for (int i = 0; i < artifact.Zones[0].Leds.Count; i++)
        {
            artifact.Zones[0].Leds[i].U = i / 11f;
            artifact.Zones[0].Leds[i].V = i / 11f;
        }
        Assert.False(MappingLint.Validate(artifact, zoneIsLinear: _ => false).Ok);
    }

    [Fact]
    public void Mostly_disabled_flags_auto_apply_ineligible_but_valid()
    {
        var artifact = Valid();
        for (int i = 0; i < 7; i++)
            artifact.Zones[0].Disabled.Add(i);
        var result = MappingLint.Validate(artifact);
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.True(result.AutoApplyIneligible);
    }

    [Fact]
    public void Invalid_group_range_fails()
    {
        var artifact = Valid();
        artifact.Zones[0].Groups.Add(new MappingGroup
        {
            Name = "Bad",
            Ranges = new() { new MappingLedRange { Start = 5, End = 99 } },
        });
        Assert.False(MappingLint.Validate(artifact).Ok);
    }

    [Fact]
    public void Duplicate_group_name_fails()
    {
        var artifact = Valid();
        artifact.Zones[0].Groups.Add(new MappingGroup
        { Name = "A", Ranges = new() { new MappingLedRange { Start = 0, End = 1 } } });
        artifact.Zones[0].Groups.Add(new MappingGroup
        { Name = "A", Ranges = new() { new MappingLedRange { Start = 2, End = 3 } } });
        Assert.False(MappingLint.Validate(artifact).Ok);
    }

    [Fact]
    public void Duplicate_zone_index_fails()
    {
        var artifact = Valid();
        var clone = Valid();
        artifact.Zones.Add(clone.Zones[0]);
        Assert.False(MappingLint.Validate(artifact).Ok);
    }
}
