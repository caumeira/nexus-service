using System;
using Nexus.Service.Peripherals.LianLi;

namespace Nexus.Service.Tests.LianLi;

public class LianLiProtocolTests
{
    // ── Constants ──

    [Fact]
    public void Constants_match_known_values()
    {
        Assert.Equal(0x0CF2, LianLiProtocol.VendorId);
        Assert.Equal(0xA102, LianLiProtocol.ProductId);
        Assert.Equal(0xFF72, LianLiProtocol.VendorUsagePage);
        Assert.Equal(0xA1, LianLiProtocol.VendorUsage);
        Assert.Equal(4, LianLiProtocol.PortCount);
        Assert.Equal(8, LianLiProtocol.ChannelCount);
        Assert.Equal(16, LianLiProtocol.InnerLedsPerFan);
        Assert.Equal(16, LianLiProtocol.OuterLedsPerFan);
        Assert.Equal(16, LianLiProtocol.LedsPerFanPerChannel);
        Assert.Equal(4, LianLiProtocol.MaxFansPerPort);
        Assert.Equal(7, LianLiProtocol.FeatureReportSize);
        Assert.Equal(353, LianLiProtocol.OutputReportSize);
        Assert.Equal(65, LianLiProtocol.InputReportSize);
        Assert.Equal(0xE0, LianLiProtocol.ReportId);
    }

    // ── BuildSetQuantity ──

    [Theory]
    [InlineData(0, 3, new byte[] { 0xE0, 0x10, 0x60, 0x01, 0x03, 0x00, 0x00 })]
    [InlineData(1, 2, new byte[] { 0xE0, 0x10, 0x60, 0x02, 0x02, 0x00, 0x00 })]
    [InlineData(3, 0, new byte[] { 0xE0, 0x10, 0x60, 0x04, 0x00, 0x00, 0x00 })]
    public void BuildSetQuantity_emits_correct_bytes(int group, int qty, byte[] expected)
    {
        Assert.Equal(expected, LianLiProtocol.BuildSetQuantity(group, qty));
    }

    [Fact]
    public void BuildSetQuantity_clamps_qty_to_four()
    {
        var report = LianLiProtocol.BuildSetQuantity(0, 99);
        Assert.Equal(4, report[4]);
    }

    // ── BuildEffectCommit ──

    [Fact]
    public void BuildEffectCommit_encodes_channel_in_nibble()
    {
        var report = LianLiProtocol.BuildEffectCommit(3, 0x01, 0x00, 0x00, 0x00);
        Assert.Equal(7, report.Length);
        Assert.Equal(0xE0, report[0]);
        Assert.Equal(0x13, report[1]); // 0x10 | ch
        Assert.Equal(0x01, report[2]); // STATIC_COLOR
    }

    // ── BuildFrameLatch ──

    [Fact]
    public void BuildFrameLatch_is_E0_60_00_01_00_00_00()
    {
        Assert.Equal(
            new byte[] { 0xE0, 0x60, 0x00, 0x01, 0x00, 0x00, 0x00 },
            LianLiProtocol.BuildFrameLatch());
    }

    // ── BuildManualMode ──

    [Theory]
    [InlineData(0, 0x10)]
    [InlineData(1, 0x20)]
    [InlineData(2, 0x40)]
    [InlineData(3, 0x80)]
    public void BuildManualMode_selector_byte(int ch, int expected)
    {
        var report = LianLiProtocol.BuildManualMode(ch);
        Assert.Equal(0xE0, report[0]);
        Assert.Equal(0x10, report[1]);
        Assert.Equal(0x62, report[2]);
        Assert.Equal((byte)expected, report[3]);
    }

    // ── BuildReleaseMode ──

    [Theory]
    [InlineData(0, 0x11)]
    [InlineData(1, 0x22)]
    [InlineData(2, 0x44)]
    [InlineData(3, 0x88)]
    public void BuildReleaseMode_selector_byte(int ch, int expected)
    {
        var report = LianLiProtocol.BuildReleaseMode(ch);
        Assert.Equal((byte)expected, report[3]);
    }

    // ── BuildSetSpeed ──

    [Fact]
    public void BuildSetSpeed_port_in_second_byte()
    {
        var report = LianLiProtocol.BuildSetSpeed(2, 50);
        Assert.Equal(0xE0, report[0]);
        Assert.Equal(0x22, report[1]);
        Assert.Equal(0x00, report[2]);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 10)]
    [InlineData(9, 10)]
    [InlineData(10, 10)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    public void BuildSetSpeed_duty_byte_encoding(int duty, int expectedByte)
    {
        var report = LianLiProtocol.BuildSetSpeed(0, duty);
        Assert.Equal((byte)expectedByte, report[3]);
    }

    // ── BuildRpmPrimer ──

    [Fact]
    public void BuildRpmPrimer_is_E0_50_00_00_00_00_00()
    {
        Assert.Equal(
            new byte[] { 0xE0, 0x50, 0x00, 0x00, 0x00, 0x00, 0x00 },
            LianLiProtocol.BuildRpmPrimer());
    }

    // ── DutyByte ──

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 10)]
    [InlineData(9, 10)]
    [InlineData(10, 10)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    public void DutyByte_mapping(int duty, int expected)
    {
        Assert.Equal((byte)expected, LianLiProtocol.DutyByte(duty));
    }

    // ── DecodeRpm ──

    [Fact]
    public void DecodeRpm_big_endian_two_bytes_per_channel()
    {
        // buf[0] = report id; ch=0: buf[1]=high, buf[2]=low -> rpm=0x04B0=1200
        Span<byte> buf = stackalloc byte[LianLiProtocol.InputReportSize];
        buf[1] = 0x04;
        buf[2] = 0xB0;
        Assert.Equal(1200, LianLiProtocol.DecodeRpm(buf, 0));
    }

    [Fact]
    public void DecodeRpm_channel_offset()
    {
        // ch=2: buf[1+4]=buf[5]=high, buf[6]=low -> rpm=0x0546=1350
        Span<byte> buf = stackalloc byte[LianLiProtocol.InputReportSize];
        buf[5] = 0x05;
        buf[6] = 0x46;
        Assert.Equal(1350, LianLiProtocol.DecodeRpm(buf, 2));
    }

    [Fact]
    public void DecodeRpm_returns_negative_one_for_out_of_range()
    {
        Span<byte> buf = stackalloc byte[LianLiProtocol.InputReportSize];
        buf[1] = 0xFF;
        buf[2] = 0xFF;
        Assert.Equal(-1, LianLiProtocol.DecodeRpm(buf, 0));
    }

    [Fact]
    public void DecodeRpm_accepts_zero()
    {
        Span<byte> buf = stackalloc byte[LianLiProtocol.InputReportSize];
        Assert.Equal(0, LianLiProtocol.DecodeRpm(buf, 0));
    }

    // ── BuildColorData ──

    [Fact]
    public void BuildColorData_length_is_OutputReportSize()
    {
        Assert.Equal(LianLiProtocol.OutputReportSize, LianLiProtocol.BuildColorData(0, ReadOnlySpan<byte>.Empty).Length);
    }

    [Fact]
    public void BuildColorData_report_id_and_channel_nibble()
    {
        var report = LianLiProtocol.BuildColorData(5, ReadOnlySpan<byte>.Empty);
        Assert.Equal(0xE0, report[0]);
        Assert.Equal(0x35, report[1]);
    }

    [Fact]
    public void BuildColorData_swaps_green_and_blue()
    {
        // Input: R=0x10, G=0x20, B=0x30. Wire order: R, B, G.
        byte[] leds = { 0x10, 0x20, 0x30 };
        var report = LianLiProtocol.BuildColorData(0, leds);
        Assert.Equal(0x10, report[2]); // R unchanged
        Assert.Equal(0x30, report[3]); // B in wire-G slot
        Assert.Equal(0x20, report[4]); // G in wire-B slot
    }

    [Fact]
    public void BuildColorData_energy_cap_scales_proportionally()
    {
        // R=200, G=200, B=200 -> sum=600 > 460; should be scaled down.
        byte[] leds = { 200, 200, 200 };
        var report = LianLiProtocol.BuildColorData(0, leds);
        // All three bytes must be equal (proportional scaling).
        Assert.Equal(report[2], report[3]);
        Assert.Equal(report[2], report[4]);
        // Each must be less than 200.
        Assert.True(report[2] < 200);
    }

    [Fact]
    public void BuildColorData_no_cap_below_limit()
    {
        // R=100, G=100, B=100 -> sum=300 < 460; no capping.
        byte[] leds = { 100, 100, 100 };
        var report = LianLiProtocol.BuildColorData(0, leds);
        Assert.Equal(100, report[2]);
        Assert.Equal(100, report[3]);
        Assert.Equal(100, report[4]);
    }

    // ── LianLiSettings ──

    [Fact]
    public void LianLiSettings_GetFans_reads_per_port()
    {
        var s = new Nexus.Service.Persistence.LianLiSettings
        {
            Port0Fans = 3,
            Port1Fans = 2,
            Port2Fans = 0,
            Port3Fans = 1,
        };
        Assert.Equal(3, s.GetFans(0));
        Assert.Equal(2, s.GetFans(1));
        Assert.Equal(0, s.GetFans(2));
        Assert.Equal(1, s.GetFans(3));
        Assert.Equal(0, s.GetFans(99));
    }

    [Fact]
    public void LianLiSettings_SetFans_writes_per_port()
    {
        var s = new Nexus.Service.Persistence.LianLiSettings();
        s.SetFans(0, 4);
        s.SetFans(2, 3);
        Assert.Equal(4, s.Port0Fans);
        Assert.Equal(0, s.Port1Fans);
        Assert.Equal(3, s.Port2Fans);
        Assert.Equal(0, s.Port3Fans);
    }
}
