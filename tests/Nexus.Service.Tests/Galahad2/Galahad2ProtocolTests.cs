using Nexus.Service.Peripherals.Galahad2;
using Nexus.Service.Peripherals.LianLiCp;

namespace Nexus.Service.Tests.Galahad2;

public class Galahad2ProtocolTests
{
    // ---- EncodeSetFan (cmd 0x8B) ----

    [Fact]
    public void EncodeSetFan_sets_command_byte()
    {
        var packet = Galahad2Protocol.EncodeSetFan(50);
        Assert.Equal(0x8B, CommandPacket.CommandOf(packet));
    }

    [Fact]
    public void EncodeSetFan_syncFlag_is_zero()
    {
        var packet = Galahad2Protocol.EncodeSetFan(80);
        Assert.Equal(0x00, packet[CommandPacket.PayloadOffset]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(-10, 0)]
    [InlineData(110, 100)]
    public void EncodeSetFan_clamps_duty_to_0_100(int input, int expectedDuty)
    {
        var packet = Galahad2Protocol.EncodeSetFan(input);
        Assert.Equal((byte)expectedDuty, packet[CommandPacket.PayloadOffset + 1]);
    }

    // ---- EncodeSetPump (cmd 0x8A) ----

    [Fact]
    public void EncodeSetPump_sets_command_byte()
    {
        var packet = Galahad2Protocol.EncodeSetPump(50);
        Assert.Equal(0x8A, CommandPacket.CommandOf(packet));
    }

    [Fact]
    public void EncodeSetPump_syncFlag_is_zero()
    {
        var packet = Galahad2Protocol.EncodeSetPump(70);
        Assert.Equal(0x00, packet[CommandPacket.PayloadOffset]);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(25, 50)]
    [InlineData(49, 50)]
    [InlineData(50, 50)]
    [InlineData(75, 75)]
    [InlineData(100, 100)]
    [InlineData(110, 100)]
    public void EncodeSetPump_floors_duty_at_50(int input, int expectedDuty)
    {
        var packet = Galahad2Protocol.EncodeSetPump(input);
        Assert.Equal((byte)expectedDuty, packet[CommandPacket.PayloadOffset + 1]);
    }

    // ---- EncodeHandshakeRequest (cmd 0x81) ----

    [Fact]
    public void EncodeHandshakeRequest_sets_command_byte()
    {
        var packet = Galahad2Protocol.EncodeHandshakeRequest();
        Assert.Equal(0x81, CommandPacket.CommandOf(packet));
    }

    [Fact]
    public void EncodeHandshakeRequest_has_zero_payload_length()
    {
        var packet = Galahad2Protocol.EncodeHandshakeRequest();
        Assert.Equal(0, CommandPacket.PayloadLengthOf(packet));
    }

    // ---- DecodeHandshake ----

    [Fact]
    public void DecodeHandshake_decodes_fan_and_pump_rpm_BE16()
    {
        // fanRpm=1200 (0x04B0), pumpRpm=2400 (0x0960)
        var reply = BuildHandshakeReply(fanRpm: 1200, pumpRpm: 2400);
        var reading = Galahad2Protocol.DecodeHandshake(reply);
        Assert.NotNull(reading);
        Assert.Equal(1200, reading.Value.FanRpm);
        Assert.Equal(2400, reading.Value.PumpRpm);
    }

    [Fact]
    public void DecodeHandshake_returns_null_when_payload_too_short()
    {
        var packet = new byte[CommandPacket.Length];
        packet[0] = CommandPacket.ReportId;
        packet[1] = 0x81;
        packet[5] = 2; // Only 2 payload bytes; need at least 4.
        var reading = Galahad2Protocol.DecodeHandshake(packet);
        Assert.Null(reading);
    }

    [Fact]
    public void DecodeHandshake_handles_zero_rpm()
    {
        var reply = BuildHandshakeReply(fanRpm: 0, pumpRpm: 0);
        var reading = Galahad2Protocol.DecodeHandshake(reply);
        Assert.NotNull(reading);
        Assert.Equal(0, reading.Value.FanRpm);
        Assert.Equal(0, reading.Value.PumpRpm);
    }

    [Fact]
    public void DecodeHandshake_handles_max_plausible_rpm()
    {
        // 6000 = 0x1770
        var reply = BuildHandshakeReply(fanRpm: 6000, pumpRpm: 6000);
        var reading = Galahad2Protocol.DecodeHandshake(reply);
        Assert.NotNull(reading);
        Assert.Equal(6000, reading.Value.FanRpm);
        Assert.Equal(6000, reading.Value.PumpRpm);
    }

    // Builds a 64-byte handshake reply with 4-byte payload [fanRpm_hi, fanRpm_lo, pumpRpm_hi, pumpRpm_lo].
    private static byte[] BuildHandshakeReply(int fanRpm, int pumpRpm)
    {
        var packet = new byte[CommandPacket.Length];
        packet[0] = CommandPacket.ReportId;
        packet[1] = 0x81;
        packet[5] = 4; // payload length
        int offset = CommandPacket.PayloadOffset;
        packet[offset] = (byte)(fanRpm >> 8);
        packet[offset + 1] = (byte)(fanRpm & 0xFF);
        packet[offset + 2] = (byte)(pumpRpm >> 8);
        packet[offset + 3] = (byte)(pumpRpm & 0xFF);
        return packet;
    }
}
