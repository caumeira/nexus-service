using System;
using System.Linq;
using Nexus.Service.Peripherals.Nzxt;
using Xunit;

namespace Nexus.Service.Tests;

public class KrakenProtocolTests
{
    // Reply frames captured from the physical Kraken Elite V2 (1E71:3012, firmware
    // 1.2.0). Any change to the decoders must be re-verified against real hardware.

    private static byte[] Frame(params byte[] head)
    {
        var buf = new byte[KrakenProtocol.ReportLength];
        head.CopyTo(buf, 0);
        return buf;
    }

    [Fact]
    public void DecodeStatus_matches_captured_frame()
    {
        var report = Frame(
            0x75, 0x01, 0x28, 0x63, 0x94, 0x82, 0x0e, 0xc0, 0x2d, 0x39, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x1b, 0x01, 0xb5, 0x05, 0x14, 0x19, 0x01, 0x02, 0xee, 0x01, 0x14, 0x14);

        var reading = KrakenProtocol.DecodeStatus(report);

        Assert.NotNull(reading);
        Assert.Equal(27.1, reading!.Value.LiquidTempC, 3);
        Assert.Equal(1461, reading.Value.PumpRpm);
        Assert.Equal(20, reading.Value.PumpDuty);
        Assert.Equal(494, reading.Value.FanRpm);
        Assert.Equal(20, reading.Value.FanDuty);
    }

    [Fact]
    public void DecodeStatus_rejects_the_firmware_fault_marker()
    {
        var report = Frame(0x75, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01, 0xFF, 0xFF);
        Assert.Null(KrakenProtocol.DecodeStatus(report));
    }

    [Fact]
    public void DecodeStatus_accepts_the_unsolicited_push_subcommand()
    {
        // The cooler pushes 0x75 0x02 about once a second without being asked.
        var report = Frame(
            0x75, 0x02, 0x28, 0x63, 0x94, 0x82, 0x0e, 0xc0, 0x2d, 0x39, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x1b, 0x01, 0xb5, 0x05, 0x14, 0x19, 0x01, 0x02, 0xee, 0x01, 0x14, 0x14);

        Assert.NotNull(KrakenProtocol.DecodeStatus(report));
    }

    [Fact]
    public void DecodeLcdInfo_reads_geometry_and_backlight_from_the_device()
    {
        var report = Frame(
            0x31, 0x01, 0x28, 0x63, 0x94, 0x82, 0x0e, 0xc0, 0x2d, 0x39, 0x00, 0x00, 0x00, 0x00,
            0x05, 0x00, 0x80, 0x00, 0x00, 0x10, 0x80, 0x02, 0x80, 0x02, 0x50, 0x01, 0x00, 0xff);

        var info = KrakenProtocol.DecodeLcdInfo(report);

        Assert.NotNull(info);
        Assert.Equal(640, info!.Value.Width);
        Assert.Equal(640, info.Value.Height);
        Assert.Equal(80, info.Value.BrightnessPercent);
        Assert.Equal(0, info.Value.OrientationQuarterTurns);
    }

    [Fact]
    public void DecodeFirmware_reads_1_2_0()
    {
        var report = Frame(
            0x11, 0x01, 0x28, 0x63, 0x94, 0x82, 0x0e, 0xc0, 0x2d, 0x39, 0x00, 0x00, 0x00, 0x00,
            0x12, 0x30, 0x01, 0x01, 0x02, 0x00);

        var fw = KrakenProtocol.DecodeFirmware(report);

        Assert.NotNull(fw);
        Assert.Equal("1.2.0", fw!.Value.ToString());
    }

    [Fact]
    public void DecodeAccessories_finds_the_ring_and_the_fan_chain()
    {
        // Two channels: slot 0 of channel 0 is the Elite ring, slot 0 of channel 1 the fans.
        var report = Frame(
            0x21, 0x03, 0x28, 0x63, 0x94, 0x82, 0x0e, 0xc0, 0x2d, 0x39, 0x00, 0x00, 0x00, 0x00,
            0x02, 0x1e, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1b);

        Assert.Equal(2, KrakenProtocol.DecodeChannelCount(report));
        Assert.Equal(0x1E, KrakenProtocol.DecodeAccessory(report, 0, 0));
        Assert.Equal(0x1B, KrakenProtocol.DecodeAccessory(report, 1, 0));
        Assert.Equal(24, KrakenProtocol.LedCountForAccessory(0x1E));
        Assert.Equal(16, KrakenProtocol.LedCountForAccessory(0x1B));
        Assert.Equal(0, KrakenProtocol.LedCountForAccessory(0x99));
    }

    [Fact]
    public void DecodeDisplayMode_reads_the_active_mode()
    {
        var report = Frame(
            0x31, 0x03, 0x28, 0x63, 0x94, 0x82, 0x0e, 0xc0, 0x2d, 0x39, 0x00, 0x00, 0x00, 0x00, 0x04);

        Assert.Equal(KrakenDisplayMode.Bucket, KrakenProtocol.DecodeDisplayMode(report));
    }

    [Fact]
    public void Every_command_fills_a_full_report()
    {
        // A short write is rejected by this firmware, so no encoder may return a stub.
        Assert.Equal(KrakenProtocol.ReportLength, KrakenProtocol.EncodeStatusRequest().Length);
        Assert.Equal(KrakenProtocol.ReportLength, KrakenProtocol.EncodeFirmwareRequest().Length);
        Assert.Equal(KrakenProtocol.ReportLength, KrakenProtocol.EncodeLcdInfoRequest().Length);
        Assert.Equal(KrakenProtocol.ReportLength, KrakenProtocol.EncodeSetBacklight(50, 1).Length);
        Assert.Equal(KrakenProtocol.ReportLength, KrakenProtocol.EncodeSetDisplayMode(KrakenDisplayMode.Liquid, 0).Length);
        Assert.Equal(KrakenProtocol.ReportLength, KrakenProtocol.EncodeDeleteBucket(3).Length);
        Assert.Equal(KrakenProtocol.ReportLength, KrakenProtocol.EncodeSetupBucket(0, 0, 1601).Length);
        Assert.Equal(KrakenProtocol.ReportLength, KrakenProtocol.EncodeFixedColor(0b001, 1, 2, 3).Length);
        Assert.All(KrakenProtocol.EncodeDirectColors(0b010, new byte[] { 1, 2, 3 }),
            r => Assert.Equal(KrakenProtocol.ReportLength, r.Length));
    }

    [Fact]
    public void EncodeSetBacklight_carries_brightness_and_rotation_together()
    {
        var report = KrakenProtocol.EncodeSetBacklight(15, 2);

        Assert.Equal(0x30, report[0]);
        Assert.Equal(0x02, report[1]);
        Assert.Equal(0x01, report[2]);
        Assert.Equal(15, report[3]);
        Assert.Equal(0x01, report[6]);
        Assert.Equal(2, report[7]);
    }

    [Fact]
    public void EncodeSetBacklight_clamps_out_of_range_brightness()
    {
        Assert.Equal(100, KrakenProtocol.EncodeSetBacklight(250, 0)[3]);
        Assert.Equal(0, KrakenProtocol.EncodeSetBacklight(-5, 0)[3]);
    }

    [Fact]
    public void EncodeSpeedCurve_uses_the_verified_channel_tuples()
    {
        var duties = Enumerable.Repeat((byte)55, KrakenProtocol.CurvePointCount).ToArray();

        var pump = KrakenProtocol.EncodeSpeedCurve(KrakenProtocol.PumpChannel, duties);
        Assert.Equal(0x72, pump[0]);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x00 }, pump[1..4]);
        Assert.Equal(55, pump[4]);
        Assert.Equal(55, pump[43]);
        Assert.Equal(0, pump[44]);

        var fan = KrakenProtocol.EncodeSpeedCurve(KrakenProtocol.FanChannel, duties);
        Assert.Equal(new byte[] { 0x02, 0x01, 0x01 }, fan[1..4]);
    }

    [Fact]
    public void EncodeSpeedCurve_rejects_a_wrong_length_curve()
    {
        Assert.Throws<ArgumentException>(() =>
            KrakenProtocol.EncodeSpeedCurve(KrakenProtocol.PumpChannel, new byte[10]));
    }

    [Fact]
    public void EncodeFixedColor_writes_grb_not_rgb()
    {
        var report = KrakenProtocol.EncodeFixedColor(KrakenProtocol.ColorChannelRing, r: 0x11, g: 0x22, b: 0x33);

        Assert.Equal(0x2A, report[0]);
        Assert.Equal(0x04, report[1]);
        Assert.Equal(KrakenProtocol.ColorChannelRing, report[2]);
        Assert.Equal(KrakenProtocol.ColorChannelRing, report[3]);
        Assert.Equal(0x22, report[7]);  // G
        Assert.Equal(0x11, report[8]);  // R
        Assert.Equal(0x33, report[9]);  // B
        Assert.Equal(1, report[7 + (16 * 3) + 1]); // colour count
    }

    [Fact]
    public void EncodeColors_adds_two_to_the_direction_byte_when_backward()
    {
        const int footer = 7 + (16 * 3);
        var forward = KrakenProtocol.EncodeColors(
            0b001, KrakenColorMode.SpectrumWave, KrakenAnimationSpeed.Normal, ReadOnlySpan<byte>.Empty, forward: true);
        var backward = KrakenProtocol.EncodeColors(
            0b001, KrakenColorMode.SpectrumWave, KrakenAnimationSpeed.Normal, ReadOnlySpan<byte>.Empty, forward: false);

        Assert.Equal(0x00, forward[footer]);
        Assert.Equal(0x02, backward[footer]);

        // Marquee carries a non-zero base even running forward.
        var marquee = KrakenProtocol.EncodeColors(
            0b001, KrakenColorMode.CoveringMarquee, KrakenAnimationSpeed.Normal, new byte[] { 1, 2, 3 }, forward: true);
        Assert.Equal(0x04, marquee[footer]);
    }

    [Fact]
    public void EncodeDirectColors_emits_table_latch_and_apply_in_order()
    {
        var rgb = new byte[] { 0xAA, 0xBB, 0xCC, 0x10, 0x20, 0x30 };

        var reports = KrakenProtocol.EncodeDirectColors(KrakenProtocol.ColorChannelFans, rgb);

        Assert.Equal(3, reports.Length);

        Assert.Equal(0x22, reports[0][0]);
        Assert.Equal(0x10, reports[0][1]);
        Assert.Equal(KrakenProtocol.ColorChannelFans, reports[0][2]);
        // GRB swap on both colours.
        Assert.Equal(new byte[] { 0xBB, 0xAA, 0xCC, 0x20, 0x10, 0x30 }, reports[0][4..10]);
        // Unused slots stay zero so stale colours cannot linger.
        Assert.Equal(0, reports[0][10]);

        Assert.Equal(0x22, reports[1][0]);
        Assert.Equal(0x11, reports[1][1]);

        Assert.Equal(0x22, reports[2][0]);
        Assert.Equal(0xA0, reports[2][1]);
        Assert.Equal(0x01, reports[2][4]);
    }

    [Fact]
    public void EncodeDirectColors_drops_colours_past_the_channel_limit()
    {
        var rgb = new byte[(KrakenProtocol.MaxDirectColors + 5) * 3];
        rgb.AsSpan().Fill(0x7F);

        var table = KrakenProtocol.EncodeDirectColors(0b001, rgb)[0];

        var lastSlot = 4 + ((KrakenProtocol.MaxDirectColors - 1) * 3);
        Assert.Equal(0x7F, table[lastSlot]);
        Assert.Equal(0, table[lastSlot + 3]);
    }

    [Fact]
    public void EncodeBulkHeader_is_the_magic_plus_format_and_little_endian_length()
    {
        var header = KrakenProtocol.EncodeBulkHeader(KrakenProtocol.BulkFormatRgba8888, 1_638_400);

        Assert.Equal(20, header.Length);
        Assert.Equal(
            new byte[] { 0x12, 0xFA, 0x01, 0xE8, 0xAB, 0xCD, 0xEF, 0x98, 0x76, 0x54, 0x32, 0x10 },
            header[..12]);
        Assert.Equal(0x02, header[12]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x19, 0x00 }, header[16..20]);
    }

    [Fact]
    public void PagesFor_rounds_the_frame_up_to_whole_pages()
    {
        // A full RGBA frame plus the 20-byte header spans 1601 KiB pages.
        Assert.Equal(1601, KrakenProtocol.PagesFor(KrakenProtocol.LcdFrameBytes));
        Assert.Equal(1, KrakenProtocol.PagesFor(1));
    }

    [Fact]
    public void LcdFrameBytes_is_one_full_rgba_panel()
    {
        Assert.Equal(640 * 640 * 4, KrakenProtocol.LcdFrameBytes);
    }

    [Fact]
    public void IsBucketEmpty_ignores_the_echoed_index()
    {
        var empty = Frame(0x31, 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x07);
        Assert.True(KrakenProtocol.IsBucketEmpty(empty));

        // An occupied bucket reports its asset index, start page and size.
        var occupied = Frame(
            0x31, 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0x00, 0x01, 0x02, 0x00, 0x00, 0x41, 0x06, 0x01, 0x01);
        Assert.False(KrakenProtocol.IsBucketEmpty(occupied));
    }

    [Fact]
    public void IsAck_reads_byte_fourteen()
    {
        Assert.True(KrakenProtocol.IsAck(Frame(0x33, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01)));
        // 0x05 is the overlap failure the device returns for a stale bucket allocation.
        Assert.False(KrakenProtocol.IsAck(Frame(0x33, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x05)));
    }
}
