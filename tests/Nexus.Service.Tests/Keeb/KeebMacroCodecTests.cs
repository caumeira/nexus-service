using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Keeb;

/// <summary>
/// Golden vectors for the 0xF3 macro payload (hyte-refs firmware-protocol/Keeb/7-macro.md):
/// 4×65B pages, [Repeat_L, Repeat_H] then [Attribute, KeyCode] action pairs,
/// attr bit7 = press(0)/release(1), bits0-6 = delay/10ms, 0x7F = extended-delay escape.
/// </summary>
public class KeebMacroCodecTests
{
    private static byte[] FirstPageData(byte[] pages)
    {
        // page 0 = bytes 0..64; byte 0 is the report id, data starts at 1.
        var data = new byte[KeebLayout.PageDataSize];
        System.Array.Copy(pages, 1, data, 0, KeebLayout.PageDataSize);
        return data;
    }

    [Fact]
    public void BuildMacroPages_emits_4_pages_with_report_ids()
    {
        var pages = KeebMacroCodec.BuildMacroPages(new KeebMacroDocument());
        Assert.Equal(KeebMacroCodec.PageCount * KeebLayout.PageSize, pages.Length); // 260
        for (var p = 0; p < KeebMacroCodec.PageCount; p++)
            Assert.Equal(0x00, pages[p * KeebLayout.PageSize]);
    }

    [Fact]
    public void BuildMacroPages_press_then_release_with_delays()
    {
        var doc = new KeebMacroDocument
        {
            Keys =
            {
                new KeebMacroKey { Key = "KeyA", Type = "Make", Duration = 10 },
                new KeebMacroKey { Key = "KeyA", Type = "Break", Duration = 50 },
            },
        };
        var data = FirstPageData(KeebMacroCodec.BuildMacroPages(doc));

        Assert.Equal(0x01, data[0]); // Repeat_L = 1
        Assert.Equal(0x00, data[1]); // Repeat_H
        Assert.Equal(0x01, data[2]); // press, delay 1 unit (10ms)
        Assert.Equal(0x04, data[3]); // KeyA HID
        Assert.Equal(0x85, data[4]); // release(0x80) | delay 5 units (50ms)
        Assert.Equal(0x04, data[5]); // KeyA HID
        Assert.Equal(0x00, data[6]); // terminator
        Assert.Equal(0x00, data[7]);
    }

    [Fact]
    public void BuildMacroPages_extended_delay_uses_7F_escape_plus_16bit_ms()
    {
        var doc = new KeebMacroDocument
        {
            Keys = { new KeebMacroKey { Key = "KeyA", Type = "Make", Duration = 2000 } },
        };
        var data = FirstPageData(KeebMacroCodec.BuildMacroPages(doc));

        Assert.Equal(0x7F, data[2]);          // press | extended-delay escape
        Assert.Equal(0x04, data[3]);          // KeyA HID
        Assert.Equal(2000 & 0xFF, data[4]);   // ms low
        Assert.Equal((2000 >> 8) & 0xFF, data[5]); // ms high
    }

    [Fact]
    public void BuildMacroPages_skips_unmappable_keys()
    {
        var doc = new KeebMacroDocument
        {
            Keys = { new KeebMacroKey { Key = "NotAKey", Type = "Make", Duration = 10 } },
        };
        var data = FirstPageData(KeebMacroCodec.BuildMacroPages(doc));
        Assert.Equal(0x01, data[0]);
        Assert.Equal(0x00, data[2]); // no action emitted; terminator region
        Assert.Equal(0x00, data[3]);
    }
}
