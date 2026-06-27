using System;
using System.Linq;
using Nexus.Service.Lighting;

namespace Nexus.Service.Tests.LianLi;

public class LianLiLightingModesTests
{
    // ── Catalog completeness ──

    [Fact]
    public void Catalog_contains_all_expected_keys()
    {
        var keys = LianLiLightingModes.Catalog.Select(m => m.Key).ToArray();
        Assert.Contains("custom", keys);
        Assert.Contains("static", keys);
        Assert.Contains("breathing", keys);
        Assert.Contains("spectrumCycle", keys);
        Assert.Contains("rainbowWave", keys);
        Assert.Contains("staggered", keys);
        Assert.Contains("tide", keys);
        Assert.Contains("runway", keys);
        Assert.Contains("mixing", keys);
        Assert.Contains("stack", keys);
        Assert.Contains("neon", keys);
        Assert.Contains("colorCycle", keys);
        Assert.Contains("meteor", keys);
        Assert.Contains("voice", keys);
        Assert.Contains("groove", keys);
        Assert.Equal(15, keys.Length);
    }

    [Fact]
    public void Catalog_has_no_duplicate_keys()
    {
        var keys = LianLiLightingModes.Catalog.Select(m => m.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    // ── Effect bytes ──

    [Theory]
    [InlineData("custom",        0x01)]
    [InlineData("static",        0x01)]
    [InlineData("breathing",     0x02)]
    [InlineData("spectrumCycle", 0x04)]
    [InlineData("rainbowWave",   0x05)]
    [InlineData("staggered",     0x18)]
    [InlineData("tide",          0x1A)]
    [InlineData("runway",        0x1C)]
    [InlineData("mixing",        0x1E)]
    [InlineData("stack",         0x20)]
    [InlineData("neon",          0x22)]
    [InlineData("colorCycle",    0x23)]
    [InlineData("meteor",        0x24)]
    [InlineData("voice",         0x26)]
    [InlineData("groove",        0x27)]
    public void Find_returns_correct_effect_byte(string key, byte expected)
    {
        var m = LianLiLightingModes.Find(key);
        Assert.NotNull(m);
        Assert.Equal(expected, m!.EffectByte);
    }

    // ── Speed codes ──

    [Theory]
    [InlineData(0, 0x02)]
    [InlineData(1, 0x01)]
    [InlineData(2, 0x00)]
    [InlineData(3, 0xFF)]
    [InlineData(4, 0xFE)]
    public void SpeedCodes_index_to_byte(int index, byte expected)
    {
        Assert.Equal(expected, LianLiLightingModes.SpeedCodes[index]);
    }

    // ── Brightness codes ──

    [Theory]
    [InlineData(0, 0x08)] // off
    [InlineData(1, 0x03)]
    [InlineData(2, 0x02)]
    [InlineData(3, 0x01)]
    [InlineData(4, 0x00)] // full
    public void BrightnessCodes_index_to_byte(int index, byte expected)
    {
        Assert.Equal(expected, LianLiLightingModes.BrightnessCodes[index]);
    }

    // ── Direction bytes ──

    [Fact]
    public void DirectionByte_zero_is_LTR()
    {
        Assert.Equal(0x00, LianLiLightingModes.DirectionByte(0));
    }

    [Fact]
    public void DirectionByte_one_is_RTL()
    {
        Assert.Equal(0x01, LianLiLightingModes.DirectionByte(1));
    }

    [Fact]
    public void DirectionByte_other_values_return_LTR()
    {
        Assert.Equal(0x00, LianLiLightingModes.DirectionByte(99));
    }

    // ── Find ──

    [Fact]
    public void Find_returns_null_for_unknown_key()
    {
        Assert.Null(LianLiLightingModes.Find("unknown"));
        Assert.Null(LianLiLightingModes.Find(""));
    }

    // ── ColorsMax ──

    [Theory]
    [InlineData("custom", 0)]
    [InlineData("rainbowWave", 0)]
    [InlineData("spectrumCycle", 0)]
    [InlineData("neon", 0)]
    [InlineData("voice", 0)]
    [InlineData("stack", 1)]
    [InlineData("staggered", 2)]
    [InlineData("colorCycle", 3)]
    [InlineData("breathing", 6)]
    [InlineData("static", 6)]
    public void ColorsMax_per_mode(string key, int expected)
    {
        var m = LianLiLightingModes.Find(key);
        Assert.NotNull(m);
        Assert.Equal(expected, m!.ColorsMax);
    }

    // ── HasSpeed / HasDirection ──

    [Fact]
    public void Custom_and_static_have_no_speed_or_direction()
    {
        var custom = LianLiLightingModes.Find("custom");
        var stat = LianLiLightingModes.Find("static");
        Assert.NotNull(custom); Assert.False(custom!.HasSpeed); Assert.False(custom.HasDirection);
        Assert.NotNull(stat);   Assert.False(stat!.HasSpeed);   Assert.False(stat.HasDirection);
    }

    [Theory]
    [InlineData("rainbowWave")]
    [InlineData("stack")]
    [InlineData("colorCycle")]
    public void Modes_with_direction_have_it(string key)
    {
        var m = LianLiLightingModes.Find(key);
        Assert.NotNull(m);
        Assert.True(m!.HasDirection);
    }
}
