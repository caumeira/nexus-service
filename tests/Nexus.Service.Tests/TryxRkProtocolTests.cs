using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxRkProtocolTests
{
    // Byte-for-byte camera-verified reference frames, captured against the physical
    // RK-firmware Panorama panel (VID 0x391A). Any change to these bytes must be
    // re-verified against real hardware, not derived from the protobuf writer.

    [Fact]
    public void BuildHeartbeat_matches_camera_verified_frame()
    {
        byte[] expected =
        {
            0x54, 0x52, 0x59, 0x58, 0x0c, 0x00, 0x00, 0x00,
            0x0a, 0x00, 0x52, 0x08, 0x0a, 0x06, 0x68, 0x65, 0x6c, 0x6c, 0x6f, 0x3f,
        };

        var actual = TryxRkProtocol.BuildHeartbeat();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildConfig_screenOn_100_matches_camera_verified_frame()
    {
        // screen on -> f5 carries f1:1 (0x08 0x01) before f2:brightness.
        byte[] expected =
        {
            0x54, 0x52, 0x59, 0x58, 0x0b, 0x00, 0x00, 0x00,
            0x0a, 0x00, 0xc2, 0x0c, 0x06, 0x2a, 0x04, 0x08, 0x01, 0x10, 0x64,
        };

        var actual = TryxRkProtocol.BuildConfig(screenOn: true, 100);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildConfig_screenOff_50_matches_camera_verified_frame()
    {
        // screen off -> f5 omits f1; carries only f2:50 (0x10 0x32).
        byte[] expected =
        {
            0x54, 0x52, 0x59, 0x58, 0x09, 0x00, 0x00, 0x00,
            0x0a, 0x00, 0xc2, 0x0c, 0x04, 0x2a, 0x02, 0x10, 0x32,
        };

        var actual = TryxRkProtocol.BuildConfig(screenOn: false, 50);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(150, 100)]
    public void BuildConfig_clamps_out_of_range_values(int input, int clamped)
    {
        var expected = TryxRkProtocol.BuildConfig(screenOn: true, clamped);

        var actual = TryxRkProtocol.BuildConfig(screenOn: true, input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildPreset_matches_the_captured_kanali_config()
    {
        // Byte-for-byte equal to Kanali's own preset-select config (USBPcap capture,
        // default_02 wallpaper, screen on, brightness 26): f200 with f1=power-on,
        // f2=standby, f3=wallpaper (nested f3), f5=screen+brightness.
        var expected = Convert.FromHexString(
            "545259587a0000000a00c20c750a240a2264656661756c745f706f7765726f" +
            "6e2e6d70342e683236345f32323430783130383012260801122264656661756c" +
            "745f7374616e6462792e6d70342e683236345f3232343078313038301a1f1a1d" +
            "64656661756c745f30322e6d70342e683236345f3232343078313038302a0408" +
            "01101a");

        var actual = TryxRkProtocol.BuildPreset(
            TryxRkProtocol.PresetMediaFile(2), screenOn: true, 26);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PresetMediaFile_zero_pads_the_index()
    {
        Assert.Equal("default_02.mp4.h264_2240x1080", TryxRkProtocol.PresetMediaFile(2));
        Assert.Equal("default_21.mp4.h264_2240x1080", TryxRkProtocol.PresetMediaFile(21));
    }

    // BuildOverlay tests below assert structural properties of the hand-rolled
    // protobuf; they are not camera-verified reference frames like the tests above.

    [Fact]
    public void BuildOverlay_starts_with_a_valid_frame_header()
    {
        var frame = TryxRkProtocol.BuildOverlay(
            new[] { new TryxOverlayLine("CPU Temperature", "44C") }, colorRgb: 0xFFFFFF, align: "Left");

        Assert.Equal((byte)'T', frame[0]);
        Assert.Equal((byte)'R', frame[1]);
        Assert.Equal((byte)'Y', frame[2]);
        Assert.Equal((byte)'X', frame[3]);
        var payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4, 4));
        Assert.Equal((uint)(frame.Length - 8), payloadLen);
    }

    [Fact]
    public void BuildOverlay_with_empty_lines_sends_an_empty_field201()
    {
        var frame = TryxRkProtocol.BuildOverlay(System.Array.Empty<TryxOverlayLine>(), colorRgb: 0xFFFFFF, align: "Left");

        // f1{} empty (0x0a 0x00) then f201{} empty: tag (201<<3|2) varint 0xca 0x0c, length 0.
        byte[] expectedPayload = { 0x0a, 0x00, 0xca, 0x0c, 0x00 };
        Assert.Equal(expectedPayload, frame[8..]);
    }

    [Fact]
    public void BuildOverlay_contains_the_font_label_and_value_text()
    {
        var lines = new[] { new TryxOverlayLine("cpu temperature", "44°C") };

        var frame = TryxRkProtocol.BuildOverlay(lines, colorRgb: 0xFF3030, align: "Left");

        var text = Encoding.UTF8.GetString(frame);
        Assert.Contains("roboto-regular", text);
        Assert.Contains("44°C", text);
        // Labels render in their given case; the builder no longer force-uppercases.
        Assert.Contains("cpu temperature", text);
    }

    [Fact]
    public void BuildOverlay_encodes_the_color_as_a_field10_varint()
    {
        var lines = new[] { new TryxOverlayLine("GPU Usage", "62%") };

        var frame = TryxRkProtocol.BuildOverlay(lines, colorRgb: 0x40C0FF, align: "Center");

        byte[] colorTagAndValue = { 0x50, 0xff, 0x81, 0x83, 0x02 };
        Assert.True(ContainsSequence(frame, colorTagAndValue));
    }

    [Fact]
    public void BuildOverlay_emits_two_widgets_per_line()
    {
        var lines = new[]
        {
            new TryxOverlayLine("CPU Temperature", "44C"),
            new TryxOverlayLine("GPU Temperature", "50C"),
        };

        var frame = TryxRkProtocol.BuildOverlay(lines, colorRgb: 0xFFFFFF, align: "Left");
        var payload = frame[8..];

        var top = ParseLengthDelimitedFields(payload);
        var f201 = Assert.Single(top, f => f.Number == 201).Value;
        var widgets = ParseLengthDelimitedFields(f201).Where(f => f.Number == 1).ToList();

        Assert.Equal(4, widgets.Count);
        foreach (var widget in widgets)
        {
            var elems = ParseLengthDelimitedFields(widget.Value).Where(f => f.Number == 8).ToList();
            Assert.Single(elems);
        }
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                return true;
            }
        }
        return false;
    }

    // Minimal protobuf reader (varint + length-delimited fields only) used to
    // structurally verify BuildOverlay's output without hardcoding byte offsets.
    private static List<(int Number, byte[] Value)> ParseLengthDelimitedFields(byte[] buf)
    {
        var result = new List<(int, byte[])>();
        var i = 0;
        while (i < buf.Length)
        {
            var (tag, tagLen) = ReadVarint(buf, i);
            i += tagLen;
            var fieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);
            if (wireType == 0)
            {
                var (_, valueLen) = ReadVarint(buf, i);
                i += valueLen;
            }
            else
            {
                var (len, lenLen) = ReadVarint(buf, i);
                i += lenLen;
                result.Add((fieldNumber, buf[i..(i + (int)len)]));
                i += (int)len;
            }
        }
        return result;
    }

    private static (ulong Value, int Length) ReadVarint(byte[] buf, int offset)
    {
        ulong value = 0;
        var shift = 0;
        var i = offset;
        while (true)
        {
            var b = buf[i];
            value |= (ulong)(b & 0x7F) << shift;
            i++;
            if ((b & 0x80) == 0)
            {
                break;
            }
            shift += 7;
        }
        return (value, i - offset);
    }
}
