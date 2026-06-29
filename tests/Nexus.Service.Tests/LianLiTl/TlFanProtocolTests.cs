using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLiCp;
using Nexus.Service.Peripherals.LianLiTl;

namespace Nexus.Service.Tests.LianLiTl;

public class TlFanProtocolTests
{
    // Constants
    [Fact]
    public void Constants_match_known_values()
    {
        Assert.Equal(0x0416, TlFanProtocol.VendorId);
        Assert.Equal(0x7372, TlFanProtocol.ProductId);
        Assert.Equal(12, TlFanProtocol.PwmMin);
        Assert.Equal(100, TlFanProtocol.PwmMax);
        Assert.Equal(1, TlFanProtocol.PwmIdle);
    }

    // EncodeSetFanSpeed - command byte
    [Fact]
    public void EncodeSetFanSpeed_command_is_0xAA()
    {
        var pkt = TlFanProtocol.EncodeSetFanSpeed(0, 0, 50);
        Assert.Equal(0xAA, pkt[1]);
    }

    // EncodeSetFanSpeed - address packing
    [Theory]
    [InlineData(0, 0, 0x00)]
    [InlineData(1, 0, 0x10)]
    [InlineData(2, 3, 0x23)]
    [InlineData(3, 15, 0x3F)]
    public void EncodeSetFanSpeed_address_packing(int port, int fan, byte expectedAddress)
    {
        var pkt = TlFanProtocol.EncodeSetFanSpeed(port, fan, 50);
        Assert.Equal(expectedAddress, pkt[CommandPacket.PayloadOffset]);
    }

    // EncodeSetFanSpeed - PWM values
    [Theory]
    [InlineData(0,   1)]    // idle wire value
    [InlineData(-5,  1)]    // negative -> idle
    [InlineData(5,  12)]    // below min -> clamped to 12
    [InlineData(12, 12)]    // at min
    [InlineData(50, 50)]    // mid range
    [InlineData(100, 100)]  // max
    [InlineData(150, 100)]  // above max -> clamped
    public void EncodeSetFanSpeed_pwm_byte(int duty, byte expectedPwm)
    {
        var pkt = TlFanProtocol.EncodeSetFanSpeed(0, 0, duty);
        Assert.Equal(expectedPwm, pkt[CommandPacket.PayloadOffset + 1]);
    }

    // EncodeHandshakeRequest
    [Fact]
    public void EncodeHandshakeRequest_command_is_0xA1_no_payload()
    {
        var pkt = TlFanProtocol.EncodeHandshakeRequest();
        Assert.Equal(0xA1, pkt[1]);
        Assert.Equal(0, pkt[5]); // payloadLen
    }

    // EncodeMotherboardSync - command byte
    [Fact]
    public void EncodeMotherboardSync_command_is_0xB1()
    {
        var pkt = TlFanProtocol.EncodeMotherboardSync(0, 0, false);
        Assert.Equal(0xB1, pkt[1]);
    }

    // EncodeMotherboardSync - high bit on sync=true
    [Theory]
    [InlineData(0, 0, true,  0x80)]
    [InlineData(0, 0, false, 0x00)]
    [InlineData(1, 2, true,  0x92)]   // 0x80 | (1<<4) | 2
    [InlineData(1, 2, false, 0x12)]   // (1<<4) | 2
    public void EncodeMotherboardSync_address_byte(int port, int fan, bool sync, byte expected)
    {
        var pkt = TlFanProtocol.EncodeMotherboardSync(port, fan, sync);
        Assert.Equal(expected, pkt[CommandPacket.PayloadOffset]);
    }

    // DecodeHandshake - basic detection
    [Fact]
    public void DecodeHandshake_detects_one_fan()
    {
        // Craft a reply: payloadLen=3, one detected record.
        // Header 0x90 = detected(1) | port=1 | fan=0.
        // RPM = 0x04B0 = 1200.
        var reply = new byte[CommandPacket.Length];
        reply[0] = CommandPacket.ReportId;
        reply[1] = 0xA1;          // cmd echo
        reply[5] = 3;             // payloadLen
        reply[6] = 0x90;          // detected=1, port=1, fan=0
        reply[7] = 0x04;          // rpm_hi
        reply[8] = 0xB0;          // rpm_lo (1200)

        var readings = TlFanProtocol.DecodeHandshake(reply);

        Assert.Single(readings);
        Assert.Equal(1, readings[0].Port);
        Assert.Equal(0, readings[0].FanIndex);
        Assert.Equal(1200, readings[0].Rpm);
    }

    [Fact]
    public void DecodeHandshake_skips_undetected_records()
    {
        // Two records: first undetected, second detected.
        var reply = new byte[CommandPacket.Length];
        reply[5] = 6;             // 2 records
        reply[6] = 0x10;          // detected=0, port=1, fan=0 (undetected)
        reply[7] = 0x00;
        reply[8] = 0x00;
        reply[9] = 0xA0;          // detected=1, port=2, fan=0
        reply[10] = 0x05;
        reply[11] = 0x46;         // rpm = 1350

        var readings = TlFanProtocol.DecodeHandshake(reply);

        Assert.Single(readings);
        Assert.Equal(2, readings[0].Port);
        Assert.Equal(0, readings[0].FanIndex);
        Assert.Equal(1350, readings[0].Rpm);
    }

    [Fact]
    public void DecodeHandshake_empty_payload_returns_empty()
    {
        var reply = new byte[CommandPacket.Length];
        reply[5] = 0;
        var readings = TlFanProtocol.DecodeHandshake(reply);
        Assert.Empty(readings);
    }

    [Fact]
    public void DecodeHandshake_rpm_is_big_endian()
    {
        var reply = new byte[CommandPacket.Length];
        reply[5] = 3;
        reply[6] = 0x80;          // detected, port=0, fan=0
        reply[7] = 0x17;          // rpm_hi
        reply[8] = 0x70;          // rpm_lo -> 0x1770 = 6000
        var readings = TlFanProtocol.DecodeHandshake(reply);
        Assert.Single(readings);
        Assert.Equal(6000, readings[0].Rpm);
    }

    [Fact]
    public void DecodeHandshake_multiple_detected_fans()
    {
        var reply = new byte[CommandPacket.Length];
        reply[5] = 9;             // 3 records
        // Fan 1: port=0, fan=0, rpm=600
        reply[6]  = 0x80;
        reply[7]  = 0x02;
        reply[8]  = 0x58;
        // Fan 2: port=0, fan=1, rpm=700
        reply[9]  = 0x81;
        reply[10] = 0x02;
        reply[11] = 0xBC;
        // Fan 3: port=1, fan=0, undetected
        reply[12] = 0x10;
        reply[13] = 0x00;
        reply[14] = 0x00;

        var readings = TlFanProtocol.DecodeHandshake(reply);

        Assert.Equal(2, readings.Count);
        Assert.Equal(0, readings[0].Port);
        Assert.Equal(0, readings[0].FanIndex);
        Assert.Equal(600, readings[0].Rpm);
        Assert.Equal(0, readings[1].Port);
        Assert.Equal(1, readings[1].FanIndex);
        Assert.Equal(700, readings[1].Rpm);
    }

    // RPM plausibility (hub's PollRpm uses 0..6000 range)
    [Theory]
    [InlineData(0,    true)]
    [InlineData(1200, true)]
    [InlineData(6000, true)]
    [InlineData(-1,   false)]
    [InlineData(6001, false)]
    public void Rpm_plausibility_range(int rpm, bool plausible)
    {
        bool result = rpm >= 0 && rpm <= 6000;
        Assert.Equal(plausible, result);
    }
}
