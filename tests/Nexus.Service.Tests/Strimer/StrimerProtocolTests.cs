using System;
using Nexus.Service.Peripherals.Strimer;

namespace Nexus.Service.Tests.Strimer;

public class StrimerProtocolTests
{
    // ── Constants ──

    [Fact]
    public void Constants_match_known_values()
    {
        Assert.Equal(0x0CF2, StrimerProtocol.VendorId);
        Assert.Equal(0xA200, StrimerProtocol.ProductId);
        Assert.Equal(0xFF72, StrimerProtocol.VendorUsagePage);
        Assert.Equal(0xA1,   StrimerProtocol.VendorUsage);
        Assert.Equal(12, StrimerProtocol.ZoneCount);
        Assert.Equal(6,  StrimerProtocol.AtxZoneCount);
        Assert.Equal(6,  StrimerProtocol.GpuZoneCount);
        Assert.Equal(20, StrimerProtocol.AtxLedsPerZone);
        Assert.Equal(27, StrimerProtocol.GpuLedsPerZone);
        Assert.Equal(27, StrimerProtocol.MaxLedsPerZone);
    }

    // ── WriteColorData ──

    [Fact]
    public void WriteColorData_report_id_is_E0()
    {
        var report = new byte[StrimerProtocol.OutputReportSize];
        StrimerProtocol.WriteColorData(report, 0, ReadOnlySpan<byte>.Empty);
        Assert.Equal(0xE0, report[0]);
    }

    [Theory]
    [InlineData(0,  0x30)]
    [InlineData(5,  0x35)]
    [InlineData(11, 0x3B)]
    public void WriteColorData_zone_byte_is_0x30_or_zone(int zone, int expected)
    {
        var report = new byte[StrimerProtocol.OutputReportSize];
        StrimerProtocol.WriteColorData(report, zone, ReadOnlySpan<byte>.Empty);
        Assert.Equal((byte)expected, report[1]);
    }

    [Fact]
    public void WriteColorData_swaps_green_and_blue()
    {
        // Input R=0x10, G=0x20, B=0x30 -> wire: R=0x10 at [2], B=0x30 at [3], G=0x20 at [4].
        byte[] leds   = { 0x10, 0x20, 0x30 };
        var    report = new byte[StrimerProtocol.OutputReportSize];
        StrimerProtocol.WriteColorData(report, 0, leds);
        Assert.Equal(0x10, report[2]); // R unchanged
        Assert.Equal(0x30, report[3]); // B in wire slot
        Assert.Equal(0x20, report[4]); // G in wire slot
    }

    [Fact]
    public void WriteColorData_multiple_leds_swap_correctly()
    {
        byte[] leds = { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
        var report  = new byte[StrimerProtocol.OutputReportSize];
        StrimerProtocol.WriteColorData(report, 0, leds);
        // LED 0: R=0x01, B=0x03, G=0x02
        Assert.Equal(0x01, report[2]);
        Assert.Equal(0x03, report[3]);
        Assert.Equal(0x02, report[4]);
        // LED 1: R=0x04, B=0x06, G=0x05
        Assert.Equal(0x04, report[5]);
        Assert.Equal(0x06, report[6]);
        Assert.Equal(0x05, report[7]);
    }

    // ── BuildEffectCommit ──

    [Theory]
    [InlineData(0,  0x10)]
    [InlineData(5,  0x15)]
    [InlineData(11, 0x1B)]
    public void BuildEffectCommit_zone_byte_is_0x10_or_zone(int zone, int expected)
    {
        var cmd = StrimerProtocol.BuildEffectCommit(zone, 0x05, 0x00, 0x01, 0x02);
        Assert.Equal((byte)expected, cmd[1]);
    }

    [Fact]
    public void BuildEffectCommit_carries_mode_speed_dir_brightness()
    {
        var cmd = StrimerProtocol.BuildEffectCommit(3, 0x05, 0x01, 0x00, 0x03);
        Assert.Equal(0xE0, cmd[0]);
        Assert.Equal(0x13, cmd[1]); // 0x10 | 3
        Assert.Equal(0x05, cmd[2]); // mode
        Assert.Equal(0x01, cmd[3]); // speed
        Assert.Equal(0x00, cmd[4]); // dir
        Assert.Equal(0x03, cmd[5]); // brightness
        Assert.Equal(0x00, cmd[6]);
    }

    // ── BuildApplyLatch ──

    [Fact]
    public void BuildApplyLatch_exact_bytes()
    {
        Assert.Equal(
            new byte[] { 0xE0, 0x2C, 0x0F, 0xFF, 0x00, 0x00, 0x00, 0x00 },
            StrimerProtocol.BuildApplyLatch());
    }

    // ── Zone helpers ──

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    public void AtxZone_maps_seg_to_0_through_5(int seg, int expected)
    {
        Assert.Equal(expected, StrimerProtocol.AtxZone(seg));
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(5, 11)]
    public void GpuZone_maps_seg_to_6_through_11(int seg, int expected)
    {
        Assert.Equal(expected, StrimerProtocol.GpuZone(seg));
    }

    // ── LED counts ──

    [Fact]
    public void TotalAtxLeds_is_120()
    {
        Assert.Equal(120, StrimerProtocol.AtxZoneCount * StrimerProtocol.AtxLedsPerZone);
    }

    [Fact]
    public void TotalGpuLeds_is_162()
    {
        Assert.Equal(162, StrimerProtocol.GpuZoneCount * StrimerProtocol.GpuLedsPerZone);
    }

    [Fact]
    public void TotalLeds_is_282()
    {
        var total = StrimerProtocol.AtxZoneCount * StrimerProtocol.AtxLedsPerZone
                  + StrimerProtocol.GpuZoneCount * StrimerProtocol.GpuLedsPerZone;
        Assert.Equal(282, total);
    }
}
