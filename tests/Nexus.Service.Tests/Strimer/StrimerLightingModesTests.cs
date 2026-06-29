using Nexus.Service.Lighting;

namespace Nexus.Service.Tests.Strimer;

public class StrimerLightingModesTests
{
    // ── Speed codes ──

    [Fact]
    public void SpeedCodes_has_five_entries_with_correct_bytes()
    {
        Assert.Equal(5, StrimerLightingModes.SpeedCodes.Length);
        Assert.Equal(0x02, StrimerLightingModes.SpeedCodes[0]);
        Assert.Equal(0x01, StrimerLightingModes.SpeedCodes[1]);
        Assert.Equal(0x00, StrimerLightingModes.SpeedCodes[2]);
        Assert.Equal(0xFE, StrimerLightingModes.SpeedCodes[3]);
        Assert.Equal(0xFF, StrimerLightingModes.SpeedCodes[4]);
    }

    // ── Brightness codes ──

    [Fact]
    public void BrightnessCodes_has_five_entries_with_correct_bytes()
    {
        Assert.Equal(5, StrimerLightingModes.BrightnessCodes.Length);
        Assert.Equal(0x08, StrimerLightingModes.BrightnessCodes[0]);
        Assert.Equal(0x03, StrimerLightingModes.BrightnessCodes[1]);
        Assert.Equal(0x02, StrimerLightingModes.BrightnessCodes[2]);
        Assert.Equal(0x01, StrimerLightingModes.BrightnessCodes[3]);
        Assert.Equal(0x00, StrimerLightingModes.BrightnessCodes[4]);
    }

    // ── Direction (inverted vs SLI) ──

    [Theory]
    [InlineData(0, 0x01)]
    [InlineData(1, 0x00)]
    public void DirectionByte_inverts_direction(int direction, byte expected)
    {
        Assert.Equal(expected, StrimerLightingModes.DirectionByte(direction));
    }

    // ── Catalog ──

    [Fact]
    public void Catalog_has_26_modes()
    {
        Assert.Equal(26, StrimerLightingModes.Catalog.Length);
    }

    [Theory]
    [InlineData("off",           0x00)]
    [InlineData("custom",        0x01)]
    [InlineData("breathing",     0x02)]
    [InlineData("flashing",      0x03)]
    [InlineData("rainbowMorph",  0x04)]
    [InlineData("rainbow",       0x05)]
    [InlineData("spectrumCycle", 0x06)]
    [InlineData("snooker",       0x19)]
    [InlineData("mixing",        0x1A)]
    [InlineData("pingPong",      0x1B)]
    [InlineData("runway",        0x1C)]
    [InlineData("painting",      0x1D)]
    [InlineData("tide",          0x1E)]
    [InlineData("blowUp",        0x1F)]
    [InlineData("meteor",        0x20)]
    [InlineData("shockWave",     0x21)]
    [InlineData("ripple",        0x22)]
    [InlineData("voice",         0x23)]
    [InlineData("bulletStack",   0x24)]
    [InlineData("drizzling",     0x25)]
    [InlineData("fadeOut",       0x26)]
    [InlineData("colorTransfer", 0x27)]
    [InlineData("crossOver",     0x28)]
    [InlineData("twinkle",       0x29)]
    [InlineData("contest",       0x2A)]
    [InlineData("parallel",      0x2B)]
    public void ModeKeys_map_to_correct_effect_bytes(string key, byte expectedByte)
    {
        var mode = StrimerLightingModes.Find(key);
        Assert.NotNull(mode);
        Assert.Equal(expectedByte, mode!.EffectByte);
    }

    [Fact]
    public void Find_returns_null_for_unknown_key()
    {
        Assert.Null(StrimerLightingModes.Find("doesNotExist"));
    }
}
