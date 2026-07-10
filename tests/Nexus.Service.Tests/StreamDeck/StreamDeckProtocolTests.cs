using System;
using System.Linq;
using System.Text;
using Nexus.Service.Peripherals.StreamDeck;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>
/// Golden-vector coverage for the Stream Deck gen1 and gen2 wire protocols.
/// Byte layouts are cross-verified against two independent MIT references:
/// python-elgato-streamdeck (StreamDeck/Devices/StreamDeckMini.py,
/// StreamDeckOriginal.py, StreamDeckOriginalV2.py, StreamDeckXL.py,
/// StreamDeckNeo.py, StreamDeckPedal.py) and the elgato-streamdeck Rust crate
/// (github.com/OpenActionAPI/rust-elgato-streamdeck: src/lib.rs, src/util.rs,
/// src/info.rs). Pure functions, no hardware, fast.
/// </summary>
public class StreamDeckProtocolTests
{
    private static readonly StreamDeckModel Mini = StreamDeckModels.ByProductId(0x0063)!;
    private static readonly StreamDeckModel Original = StreamDeckModels.ByProductId(0x0060)!;
    private static readonly StreamDeckModel Xl = StreamDeckModels.ByProductId(0x006c)!;
    private static readonly StreamDeckModel OriginalV2 = StreamDeckModels.ByProductId(0x006d)!;
    private static readonly StreamDeckModel Neo = StreamDeckModels.ByProductId(0x009a)!;
    private static readonly StreamDeckModel Pedal = StreamDeckModels.ByProductId(0x0086)!;

    // ── Feature reports ──

    [Fact]
    public void BuildResetFeature_matches_reference_bytes_at_reference_length()
    {
        var expected = new byte[] { 0x0B, 0x63, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        Assert.Equal(expected, StreamDeckProtocol.BuildResetFeature(featureReportLength: 17));
    }

    [Fact]
    public void BuildResetFeature_defaultLength_isBenchConfirmed32()
    {
        var buf = StreamDeckProtocol.BuildResetFeature();
        Assert.Equal(32, buf.Length);
        Assert.Equal(0x0B, buf[0]);
        Assert.Equal(0x63, buf[1]);
        Assert.All(buf.Skip(2), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public void BuildBrightnessFeature_matches_reference_bytes_at_reference_length(int percent)
    {
        var expected = new byte[] { 0x05, 0x55, 0xAA, 0xD1, 0x01, (byte)percent, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        Assert.Equal(expected, StreamDeckProtocol.BuildBrightnessFeature(percent, featureReportLength: 17));
    }

    [Theory]
    [InlineData(-50, 0)]
    [InlineData(150, 100)]
    public void BuildBrightnessFeature_clampsOutOfRangePercent(int input, int clamped)
    {
        var buf = StreamDeckProtocol.BuildBrightnessFeature(input, featureReportLength: 17);
        Assert.Equal((byte)clamped, buf[5]);
    }

    [Fact]
    public void BuildBrightnessFeature_defaultLength_isBenchConfirmed32()
    {
        Assert.Equal(32, StreamDeckProtocol.BuildBrightnessFeature(50).Length);
    }

    [Fact]
    public void BuildSerialFeatureRequest_reportIdIs0x03()
    {
        var buf = StreamDeckProtocol.BuildSerialFeatureRequest(featureReportLength: 17);
        Assert.Equal(17, buf.Length);
        Assert.Equal(0x03, buf[0]);
        Assert.All(buf.Skip(1), b => Assert.Equal(0, b));
    }

    [Fact]
    public void BuildFirmwareFeatureRequest_reportIdIs0x04()
    {
        var buf = StreamDeckProtocol.BuildFirmwareFeatureRequest(featureReportLength: 17);
        Assert.Equal(0x04, buf[0]);
    }

    [Fact]
    public void ExtractAsciiString_readsFromOffset5AndStripsNulPadding()
    {
        var buf = new byte[32];
        buf[0] = 0x04;
        var serial = Encoding.ASCII.GetBytes("A00DA431130Y9Y");
        serial.CopyTo(buf, 5);
        Assert.Equal("A00DA431130Y9Y", StreamDeckProtocol.ExtractAsciiString(buf));
    }

    [Fact]
    public void ExtractAsciiString_shortBufferReturnsEmpty()
    {
        Assert.Equal("", StreamDeckProtocol.ExtractAsciiString(new byte[3]));
    }

    // ── Gen1 image page chunking ──

    [Fact]
    public void BuildImagePages_Mini_singlePage_hasHeaderAndIsLastFlag()
    {
        var image = Enumerable.Range(1, 10).Select(i => (byte)i).ToArray();
        var pages = StreamDeckProtocol.BuildImagePages(image, rawKeyIndex: 2, Mini);

        Assert.Single(pages);
        var page = pages[0];
        Assert.Equal(Mini.ImageReportLength, page.Length);
        Assert.Equal(0x02, page[0]); // image report id
        Assert.Equal(0x01, page[1]); // gen1 marker
        Assert.Equal(0, page[2]);    // page number (0-based for the Mini family)
        Assert.Equal(0, page[3]);
        Assert.Equal(1, page[4]);    // is-last
        Assert.Equal(3, page[5]);    // 1-based raw key index (rawKeyIndex 2 -> 3)
        Assert.Equal(image, page.Skip(StreamDeckProtocol.PageHeaderLength).Take(image.Length).ToArray());
        // Remainder of the 1024-byte report is zero padding.
        Assert.All(page.Skip(StreamDeckProtocol.PageHeaderLength + image.Length), b => Assert.Equal(0, b));
    }

    [Fact]
    public void BuildImagePages_Mini_splitsAcrossPagesAtPayloadBoundary()
    {
        var payloadLength = Mini.ImageReportLength - StreamDeckProtocol.PageHeaderLength; // 1008
        var image = new byte[payloadLength + 1];
        for (var i = 0; i < image.Length; i++) image[i] = (byte)(i % 256);

        var pages = StreamDeckProtocol.BuildImagePages(image, rawKeyIndex: 0, Mini);

        Assert.Equal(2, pages.Count);
        Assert.Equal(0, pages[0][2]);   // page 0
        Assert.Equal(0, pages[0][4]);   // not last
        Assert.Equal(1, pages[1][2]);   // page 1
        Assert.Equal(1, pages[1][4]);   // last
        // Last page carries exactly the 1 leftover byte, rest zero.
        Assert.Equal(image[^1], pages[1][StreamDeckProtocol.PageHeaderLength]);
    }

    [Fact]
    public void BuildImagePages_Original_halvesPayloadAndNumbersPagesFrom1()
    {
        // elgato-streamdeck lib.rs WriteImageParameters::for_key: Original's
        // payload length is image_data_len / 2 (always exactly 2 pages), and
        // send_image's header closure emits (page_number + 1).
        var image = new byte[] { 10, 20, 30, 40 };

        var pages = StreamDeckProtocol.BuildImagePages(image, rawKeyIndex: 4, Original);

        Assert.Equal(2, pages.Count);
        Assert.Equal(1, pages[0][2]); // 1-based page numbering
        Assert.Equal(0, pages[0][4]); // not last
        Assert.Equal(5, pages[0][5]); // 1-based raw key index
        Assert.Equal(new byte[] { 10, 20 }, pages[0].Skip(StreamDeckProtocol.PageHeaderLength).Take(2).ToArray());

        Assert.Equal(2, pages[1][2]);
        Assert.Equal(1, pages[1][4]); // last
        Assert.Equal(new byte[] { 30, 40 }, pages[1].Skip(StreamDeckProtocol.PageHeaderLength).Take(2).ToArray());
    }

    // ── Gen1 input decode ──

    [Fact]
    public void DecodeGen1Input_Mini_readsOneByyePerKeyFromOffset1_noRemap()
    {
        var report = new byte[] { 0x01, 1, 0, 1, 0, 0, 0 };
        var states = StreamDeckProtocol.DecodeGen1Input(report, Mini);
        Assert.Equal(new[] { true, false, true, false, false, false }, states);
    }

    [Fact]
    public void DecodeGen1Input_shortReportTreatsMissingBytesAsReleased()
    {
        var report = new byte[] { 0x01, 1 }; // only key 0 present in this report
        var states = StreamDeckProtocol.DecodeGen1Input(report, Mini);
        Assert.Equal(new[] { true, false, false, false, false, false }, states);
    }

    [Fact]
    public void DecodeGen1Input_Original_appliesRightToLeftRemap()
    {
        // Original is 3 rows x 5 columns. Raw hardware index 4 (row 0, rightmost
        // physical key) is pressed; canonical key 0 (row 0, leftmost) must read
        // pressed because the firmware numbers each row right-to-left.
        var report = new byte[16];
        report[0] = 0x01;
        report[1 + 4] = 1; // raw slot 4 pressed

        var states = StreamDeckProtocol.DecodeGen1Input(report, Original);

        Assert.True(states[0]);
        for (var i = 1; i < states.Length; i++) Assert.False(states[i]);
    }

    // ── Blank image (ClearKey) ──

    [Fact]
    public void BuildBlankBmp_hasCorrectHeaderForSize80()
    {
        var bmp = StreamDeckProtocol.BuildBlankBmp(80);

        Assert.Equal(54 + 80 * 80 * 3, bmp.Length);
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal((uint)bmp.Length, BitConverter.ToUInt32(bmp, 2));
        Assert.Equal(54u, BitConverter.ToUInt32(bmp, 10));
        Assert.Equal(40u, BitConverter.ToUInt32(bmp, 14));
        Assert.Equal(80, BitConverter.ToInt32(bmp, 18));
        Assert.Equal(80, BitConverter.ToInt32(bmp, 22));
        Assert.Equal((ushort)1, BitConverter.ToUInt16(bmp, 26));
        Assert.Equal((ushort)24, BitConverter.ToUInt16(bmp, 28));
        Assert.Equal((uint)(80 * 80 * 3), BitConverter.ToUInt32(bmp, 34));
        Assert.All(bmp.Skip(54), b => Assert.Equal(0, b));
    }

    // ── Gen2 feature reports ──

    [Fact]
    public void BuildGen2ResetFeature_matchesBothReferences()
    {
        var buf = StreamDeckProtocol.BuildGen2ResetFeature();
        Assert.Equal(32, buf.Length);
        Assert.Equal(0x03, buf[0]);
        Assert.Equal(0x02, buf[1]);
        Assert.All(buf.Skip(2), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public void BuildGen2BrightnessFeature_matchesTheRustCratesCleanThirtyTwoByteBuffer(int percent)
    {
        // python's set_brightness resizes to 33 bytes via bytearray slice
        // assignment (payload[0:2] = [0x03, 0x08, percent], a 3-element list
        // replacing a 2-element slice); the Rust crate builds a clean 32-byte
        // buffer instead. This matches Rust and the existing gen1 convention.
        var buf = StreamDeckProtocol.BuildGen2BrightnessFeature(percent);
        Assert.Equal(32, buf.Length);
        Assert.Equal(0x03, buf[0]);
        Assert.Equal(0x08, buf[1]);
        Assert.Equal((byte)percent, buf[2]);
        Assert.All(buf.Skip(3), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(-50, 0)]
    [InlineData(150, 100)]
    public void BuildGen2BrightnessFeature_clampsOutOfRangePercent(int input, int clamped)
    {
        var buf = StreamDeckProtocol.BuildGen2BrightnessFeature(input);
        Assert.Equal((byte)clamped, buf[2]);
    }

    [Fact]
    public void BuildGen2SerialFeatureRequest_reportIdIs0x06()
    {
        var buf = StreamDeckProtocol.BuildGen2SerialFeatureRequest();
        Assert.Equal(0x06, buf[0]);
    }

    [Fact]
    public void BuildGen2FirmwareFeatureRequest_reportIdIs0x05()
    {
        var buf = StreamDeckProtocol.BuildGen2FirmwareFeatureRequest();
        Assert.Equal(0x05, buf[0]);
    }

    [Fact]
    public void ExtractAsciiString_gen2SerialOffsetIs2()
    {
        var buf = new byte[32];
        var serial = Encoding.ASCII.GetBytes("CL12345678");
        serial.CopyTo(buf, StreamDeckProtocol.Gen2SerialStringOffset);
        Assert.Equal("CL12345678", StreamDeckProtocol.ExtractAsciiString(buf, StreamDeckProtocol.Gen2SerialStringOffset));
    }

    [Fact]
    public void ExtractAsciiString_gen2FirmwareOffsetIs6()
    {
        var buf = new byte[32];
        var version = Encoding.ASCII.GetBytes("1.02.007");
        version.CopyTo(buf, StreamDeckProtocol.Gen2FirmwareStringOffset);
        Assert.Equal("1.02.007", StreamDeckProtocol.ExtractAsciiString(buf, StreamDeckProtocol.Gen2FirmwareStringOffset));
    }

    // ── Gen2 image page chunking ──

    [Fact]
    public void BuildGen2ImagePages_Xl_singlePage_hasEightByteHeaderAndIsLastFlag()
    {
        var image = Enumerable.Range(1, 10).Select(i => (byte)i).ToArray();
        var pages = StreamDeckProtocol.BuildGen2ImagePages(image, rawKeyIndex: 5, Xl);

        Assert.Single(pages);
        var page = pages[0];
        Assert.Equal(Xl.ImageReportLength, page.Length);
        Assert.Equal(0x02, page[0]); // image report id
        Assert.Equal(0x07, page[1]); // gen2 key-image subtype
        Assert.Equal(5, page[2]);    // 0-based raw key index, no +1
        Assert.Equal(1, page[3]);    // is-last
        Assert.Equal((ushort)image.Length, BitConverter.ToUInt16(page, 4)); // little-endian length
        Assert.Equal((ushort)0, BitConverter.ToUInt16(page, 6));            // little-endian page number
        Assert.Equal(image, page.Skip(StreamDeckProtocol.Gen2PageHeaderLength).Take(image.Length).ToArray());
        Assert.All(page.Skip(StreamDeckProtocol.Gen2PageHeaderLength + image.Length), b => Assert.Equal(0, b));
    }

    [Fact]
    public void BuildGen2ImagePages_Xl_splitsAcrossPagesAtPayloadBoundary()
    {
        var payloadLength = Xl.ImageReportLength - StreamDeckProtocol.Gen2PageHeaderLength; // 1016
        var image = new byte[payloadLength + 1];
        for (var i = 0; i < image.Length; i++) image[i] = (byte)(i % 256);

        var pages = StreamDeckProtocol.BuildGen2ImagePages(image, rawKeyIndex: 0, Xl);

        Assert.Equal(2, pages.Count);
        Assert.Equal((ushort)0, BitConverter.ToUInt16(pages[0], 6)); // page 0
        Assert.Equal(0, pages[0][3]);                                // not last
        Assert.Equal((ushort)1, BitConverter.ToUInt16(pages[1], 6)); // page 1
        Assert.Equal(1, pages[1][3]);                                // last
        Assert.Equal(image[^1], pages[1][StreamDeckProtocol.Gen2PageHeaderLength]);
    }

    [Fact]
    public void BuildGen2ImagePages_OriginalV2_usesTheSameEightByteHeaderShapeAsXl()
    {
        var image = new byte[] { 1, 2, 3 };
        var pages = StreamDeckProtocol.BuildGen2ImagePages(image, rawKeyIndex: 2, OriginalV2);

        Assert.Single(pages);
        Assert.Equal(OriginalV2.ImageReportLength, pages[0].Length);
        Assert.Equal(0x07, pages[0][1]);
        Assert.Equal(2, pages[0][2]);
    }

    // ── Gen2 input decode ──

    [Fact]
    public void DecodeGen2Input_Xl_readsOneByteAfterTheFourByteHeader()
    {
        var report = new byte[4 + Xl.KeyCount];
        report[4 + 3] = 1; // key 3 pressed
        report[4 + 10] = 1; // key 10 pressed

        var states = StreamDeckProtocol.DecodeGen2Input(report, Xl);

        Assert.Equal(Xl.KeyCount, states.Length);
        Assert.True(states[3]);
        Assert.True(states[10]);
        Assert.False(states[0]);
    }

    [Fact]
    public void DecodeGen2Input_shortReportTreatsMissingBytesAsReleased()
    {
        var report = new byte[] { 0x01, 0, 0, 0, 1 }; // only key 0 present in this report
        var states = StreamDeckProtocol.DecodeGen2Input(report, Xl);
        Assert.True(states[0]);
        Assert.All(states.Skip(1), s => Assert.False(s));
    }

    [Fact]
    public void DecodeGen2Input_Neo_onlyDecodesTheEightLcdKeys_notTheTwoTouchKeys()
    {
        // Neo's KeyCount is 8 (the LCD keys); the touch keys land at report
        // offsets KeyCount and KeyCount+1 per both references but this pass
        // does not surface them as bindable input (see StreamDeckModels.cs).
        var report = new byte[4 + Neo.KeyCount + 2];
        report[4 + 7] = 1;      // last LCD key pressed
        report[4 + 8] = 1;      // first touch key - not decoded
        var states = StreamDeckProtocol.DecodeGen2Input(report, Neo);

        Assert.Equal(8, states.Length);
        Assert.True(states[7]);
    }

    [Fact]
    public void DecodeGen2Input_Pedal_usesTheSameHeaderShapeAsVisualModels()
    {
        var report = new byte[4 + Pedal.KeyCount];
        report[4 + 1] = 1;
        var states = StreamDeckProtocol.DecodeGen2Input(report, Pedal);
        Assert.Equal(new[] { false, true, false }, states);
    }
}
