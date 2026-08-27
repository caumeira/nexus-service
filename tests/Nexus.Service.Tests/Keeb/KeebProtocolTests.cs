using System.Linq;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Tests.Keeb;

/// <summary>
/// Golden-vector coverage for the HYTE Keeb TKL wire protocol. The RGB stream
/// format is taken from the shipping OpenRGB controller
/// (nexus-rgb/openrgb-headless HYTEKeyboardController.cpp) - the path that
/// already lights real hardware - so our direct-HID stream stays byte-identical.
/// Pure functions, no hardware, fast.
/// </summary>
public class KeebProtocolTests
{
    [Fact]
    public void KeyboardStreamFeature_is_00_04_F0_padded_to_9()
    {
        Assert.Equal(new byte[] { 0x00, 0x04, 0xF0, 0, 0, 0, 0, 0, 0 }, KeebProtocol.KeyboardStreamFeature);
    }

    [Fact]
    public void SurroundStreamFeature_is_00_04_F1_padded_to_9()
    {
        Assert.Equal(new byte[] { 0x00, 0x04, 0xF1, 0, 0, 0, 0, 0, 0 }, KeebProtocol.SurroundStreamFeature);
    }

    [Theory]
    [InlineData(KeebProtocol.Read, KeebProtocol.OpSettings)]
    [InlineData(KeebProtocol.Write, KeebProtocol.OpMacro)]
    public void Feature_emits_00_rw_op_then_zeros(byte rw, byte op)
    {
        var f = KeebProtocol.Feature(rw, op);
        Assert.Equal(9, f.Length);
        Assert.Equal(0x00, f[0]);
        Assert.Equal(rw, f[1]);
        Assert.Equal(op, f[2]);
        Assert.All(f.Skip(3), b => Assert.Equal(0, b));
    }

    [Fact]
    public void BuildStreamPages_keyboard_is_6_pages_of_65_with_report_id_prefix()
    {
        var wire = new RgbColor[KeebLayout.KeyWireSlots];
        wire[0] = new RgbColor(1, 2, 3);
        wire[1] = new RgbColor(4, 5, 6);
        var pages = KeebProtocol.BuildStreamPages(wire, KeebLayout.KeyPageCount);

        Assert.Equal(KeebLayout.KeyPageCount * KeebLayout.PageSize, pages.Length); // 390
        // Each page starts with the 0x00 report id.
        for (var p = 0; p < KeebLayout.KeyPageCount; p++)
            Assert.Equal(0x00, pages[p * KeebLayout.PageSize]);
        // R,G,B stream begins at page byte 1.
        Assert.Equal(new byte[] { 0x00, 1, 2, 3, 4, 5, 6 }, pages.Take(7).ToArray());
    }

    [Fact]
    public void BuildStreamPages_surround_is_3_pages_of_65()
    {
        var wire = new RgbColor[KeebLayout.SurroundWireSlots];
        var pages = KeebProtocol.BuildStreamPages(wire, KeebLayout.SurroundPageCount);
        Assert.Equal(KeebLayout.SurroundPageCount * KeebLayout.PageSize, pages.Length); // 195
    }

    [Fact]
    public void Geometry_constants_match_openrgb()
    {
        Assert.Equal(65, KeebLayout.PageSize);
        Assert.Equal(64, KeebLayout.PageDataSize);
        Assert.Equal(6, KeebLayout.KeyPageCount);
        Assert.Equal(3, KeebLayout.SurroundPageCount);
        Assert.Equal(128, KeebLayout.KeyWireSlots);
        Assert.Equal(64, KeebLayout.SurroundWireSlots);
        Assert.Equal(51, KeebLayout.SurroundLedCount); // 50 perimeter + scroll wheel; wire slots 51..63 drive nothing
        Assert.Equal(96, KeebKeyMap.Ansi.LedCount); // ANSI board; ISO is 97 (see KeebStockUvTests)
    }

    [Fact]
    public void MapKeysToWire_scatters_each_key_to_its_wire_slot()
    {
        var leds = new RgbColor[KeebKeyMap.Ansi.LedCount];
        for (var i = 0; i < leds.Length; i++) leds[i] = new RgbColor((byte)(i + 1), 0, 0);
        var wire = new RgbColor[KeebLayout.KeyWireSlots];

        KeebKeyMap.Ansi.MapKeysToWire(leds, wire);

        for (var i = 0; i < leds.Length; i++)
            Assert.Equal(leds[i], wire[KeebKeyMap.Ansi.WireValues[i]]);
        // An unwired slot stays black.
        Assert.Equal(default, wire[1]);
    }

    [Fact]
    public void ParseDeviceInfo_decodes_vid_pid_version_layout()
    {
        // VID 0x3402 (LE 02 34), PID 0x0300 (LE 00 03), fw minor 0x21 major 0x01 = "1.33", layout 0x02 = ANSI.
        var payload = new byte[] { 0x02, 0x34, 0x00, 0x03, 0x21, 0x01, 0x02 };
        var info = KeebProtocol.ParseDeviceInfo(payload);
        Assert.NotNull(info);
        Assert.Equal(0x3402, info!.Value.VendorId);
        Assert.Equal(0x0300, info.Value.ProductId);
        Assert.Equal("1.33", info.Value.FirmwareVersion);
        Assert.Equal("ANSI", info.Value.Layout);
    }

    [Fact]
    public void ParseDeviceInfo_tolerates_leading_report_id()
    {
        var payload = new byte[] { 0x00, 0x02, 0x34, 0x00, 0x03, 0x21, 0x01, 0x01 };
        var info = KeebProtocol.ParseDeviceInfo(payload);
        Assert.NotNull(info);
        Assert.Equal("ISO", info!.Value.Layout);
    }

    [Fact]
    public void ParseDeviceInfo_rejects_foreign_vendor()
    {
        var payload = new byte[] { 0xFF, 0xFF, 0x00, 0x03, 0x21, 0x01, 0x02 };
        Assert.Null(KeebProtocol.ParseDeviceInfo(payload));
    }
}
