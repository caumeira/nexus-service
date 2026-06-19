using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Keeb;

/// <summary>
/// Golden-vector coverage for the 0x06 keeb settings page. Byte offsets are
/// validated against hyte-refs firmware-protocol/Keeb/1-set-get-keyboard-setting.md
/// and the legacy KeebSettings.GetCommands().
/// </summary>
public class KeebSettingsCodecTests
{
    [Theory]
    [InlineData("Static", 0x01)]
    [InlineData("Breathe", 0x02)]
    [InlineData("Rainbow", 0x03)]
    [InlineData("Wave", 0x04)]
    [InlineData("Flow", 0x05)]
    [InlineData("PingPong", 0x06)]
    [InlineData("nonsense", 0x01)]
    public void AnimationModeByte_maps_web_effects(string mode, byte expected)
        => Assert.Equal(expected, KeebSettingsCodec.AnimationModeByte(mode));

    [Theory]
    [InlineData("Rapid", 1)]
    [InlineData("Energetic", 2)]
    [InlineData("Standard", 3)]
    [InlineData("LaidBack", 4)]
    [InlineData("Slow", 5)]
    [InlineData("Medium", 3)]
    public void SpeedByte_maps_web_speeds_fastest_is_1(string speed, byte expected)
        => Assert.Equal(expected, KeebSettingsCodec.SpeedByte(speed));

    [Theory]
    [InlineData("LeftToRight", 0)]
    [InlineData("RightToLeft", 1)]
    [InlineData("TopToBottom", 2)]
    [InlineData("BottomToTop", 3)]
    public void DirectionByte_maps_web_directions(string dir, byte expected)
        => Assert.Equal(expected, KeebSettingsCodec.DirectionByte(dir));

    [Theory]
    [InlineData("VolumeAdjustment", 0x00)]
    [InlineData("BrightnessAdjustment", 0x01)]
    [InlineData("Scale", 0x02)]
    [InlineData("AltTab", 0x03)]
    [InlineData("CtrlTab", 0x04)]
    [InlineData("ScrollX", 0x06)]
    [InlineData("ScrollY", 0x05)]
    public void RotaryFunctionByte_maps_function_names(string fn, byte expected)
        => Assert.Equal(expected, KeebSettingsCodec.RotaryFunctionByte(fn));

    [Fact]
    public void BuildSettingsPage_lays_out_every_field()
    {
        var s = new KeebSettings
        {
            RotaryRight = "VolumeAdjustment",
            RotaryLeft = "BrightnessAdjustment",
            GameMode = new KeebGameMode { WindowsKey = true, AltF4 = true, AltTab = false, ShiftTab = false },
            FirmwareLighting = new KeebFirmwareLighting
            {
                AnimationMode = "Breathe",
                Speed = "Standard",
                Direction = "TopToBottom",
                Brightness = 80,
            },
        };

        var page = KeebSettingsCodec.BuildSettingsPage(s);

        Assert.Equal(65, page.Length);
        Assert.Equal(0x00, page[0]);                 // report id
        Assert.Equal(0x01, page[1]);                 // debounce
        // Game mode: win(bit0)=1, altf4(bit2)=1, LED(bit4)=1 => 0b0001_0101 = 0x15
        Assert.Equal(0x15, page[2]);
        Assert.Equal(0x02, page[3]);                 // Breathe
        Assert.Equal(204, page[4]);                  // brightness 80% -> round(0.8*255) = 204
        Assert.Equal(0x03, page[5]);                 // Standard speed
        Assert.Equal(0x08, page[6]);                 // animated => multi-color index 8
        Assert.Equal(0x02, page[7]);                 // TopToBottom
        // Palette is the full-intensity rainbow (brightness lives in byte 4).
        Assert.Equal(new byte[] { 255, 0, 0 }, page[12..15]);
        Assert.Equal(new byte[] { 255, 125, 0 }, page[15..18]);
        Assert.Equal(0x10, page[36]);                // rotary firmware mode
        // Right encoder (Volume) at 37..40
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0xFA }, page[37..41]);
        // Left encoder (Brightness) at 41..44
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0xFA }, page[41..45]);
    }

    [Fact]
    public void BuildSettingsPage_static_uses_color_index_0()
    {
        var s = new KeebSettings { FirmwareLighting = new KeebFirmwareLighting { AnimationMode = "Static" } };
        var page = KeebSettingsCodec.BuildSettingsPage(s);
        Assert.Equal(0x01, page[3]); // Static
        Assert.Equal(0x00, page[6]); // single palette color
    }

    [Fact]
    public void BuildSettingsPage_keeps_led_master_on_with_no_game_keys_disabled()
    {
        var s = new KeebSettings { GameMode = new KeebGameMode() };
        var page = KeebSettingsCodec.BuildSettingsPage(s);
        Assert.Equal(1 << 4, page[2]); // only the LED-on bit
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(255, 100)]
    [InlineData(204, 80)]   // the page[4] value BuildSettingsPage writes for 80%
    [InlineData(128, 50)]
    [InlineData(77, 30)]    // a knob-set byte that isn't one Nexus would write
    public void BrightnessPercentFromByte_maps_device_byte_to_percent(byte raw, int expected)
        => Assert.Equal(expected, KeebSettingsCodec.BrightnessPercentFromByte(raw));

    // The connection-worker poll reads the brightness byte back and compares it to
    // the stored percent; if a value Nexus writes didn't decode to itself, every
    // poll would see a phantom change and broadcast/flatten in a loop.
    [Fact]
    public void Brightness_round_trips_through_encode_and_decode_for_every_percent()
    {
        for (var pct = 0; pct <= 100; pct++)
        {
            var page = KeebSettingsCodec.BuildSettingsPage(
                new KeebSettings { FirmwareLighting = new KeebFirmwareLighting { Brightness = pct } });
            Assert.Equal(pct, KeebSettingsCodec.BrightnessPercentFromByte(page[4]));
        }
    }

    [Fact]
    public void BuildSettingsPage_rotary_mode_byte_follows_software_flag()
    {
        var s = new KeebSettings();
        Assert.Equal(KeebSettingsCodec.RotaryModeFirmware, KeebSettingsCodec.BuildSettingsPage(s)[36]);
        Assert.Equal(KeebSettingsCodec.RotaryModeSoftware, KeebSettingsCodec.BuildSettingsPage(s, softwareRotary: true)[36]);
    }
}
