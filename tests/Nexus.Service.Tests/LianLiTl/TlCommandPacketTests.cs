using System;
using Nexus.Service.Peripherals.LianLiTl;

namespace Nexus.Service.Tests.LianLiTl;

public class TlCommandPacketTests
{
    [Fact]
    public void Build_no_payload_produces_64_bytes()
    {
        var packet = CommandPacket.Build(0xAA);
        Assert.Equal(64, packet.Length);
    }

    [Fact]
    public void Build_sets_report_id_and_command()
    {
        var packet = CommandPacket.Build(0xAA);
        Assert.Equal(0x01, packet[0]);
        Assert.Equal(0xAA, packet[1]);
    }

    [Fact]
    public void Build_byte2_is_zero()
    {
        var packet = CommandPacket.Build(0xAA, 0x12);
        Assert.Equal(0x00, packet[2]);
    }

    [Fact]
    public void Build_pktNo_bytes_are_zero()
    {
        var packet = CommandPacket.Build(0xA1);
        Assert.Equal(0x00, packet[3]);
        Assert.Equal(0x00, packet[4]);
    }

    [Fact]
    public void Build_payload_length_in_byte5()
    {
        var packet = CommandPacket.Build(0xAA, 0x12, 0x34);
        Assert.Equal(2, packet[5]);
    }

    [Fact]
    public void Build_payload_starts_at_byte6()
    {
        var packet = CommandPacket.Build(0xAA, 0xAB, 0xCD);
        Assert.Equal(0xAB, packet[6]);
        Assert.Equal(0xCD, packet[7]);
    }

    [Fact]
    public void Build_tail_is_zero_padded()
    {
        var packet = CommandPacket.Build(0xAA, 0x10);
        for (int i = 7; i < 64; i++)
        {
            Assert.Equal(0, packet[i]);
        }
    }

    [Fact]
    public void CommandOf_reads_byte1()
    {
        var packet = CommandPacket.Build(0xB1, 0x55);
        Assert.Equal(0xB1, CommandPacket.CommandOf(packet));
    }

    [Fact]
    public void PayloadLengthOf_reads_byte5()
    {
        var packet = CommandPacket.Build(0xA1);
        Assert.Equal(0, CommandPacket.PayloadLengthOf(packet));

        var packet2 = CommandPacket.Build(0xAA, 0x12, 0x34);
        Assert.Equal(2, CommandPacket.PayloadLengthOf(packet2));
    }

    [Fact]
    public void Build_round_trip_command_and_payload()
    {
        var packet = CommandPacket.Build(0xAA, 0x23, 0x64);
        Assert.Equal(0xAA, CommandPacket.CommandOf(packet));
        Assert.Equal(2, CommandPacket.PayloadLengthOf(packet));
        Assert.Equal(0x23, packet[CommandPacket.PayloadOffset]);
        Assert.Equal(0x64, packet[CommandPacket.PayloadOffset + 1]);
    }
}
