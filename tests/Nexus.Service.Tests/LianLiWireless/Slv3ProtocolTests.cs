using System;
using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3ProtocolTests
{
    private static readonly byte[] FanMac = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
    private static readonly byte[] MasterMac = { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };

    // ── Constants ──

    [Fact]
    public void Constants_match_known_values()
    {
        Assert.Equal(0x0416, Slv3Protocol.TxVendorId);
        Assert.Equal(0x8040, Slv3Protocol.TxProductId);
        Assert.Equal(0x8041, Slv3Protocol.RxProductId);
        Assert.Equal(0xE304, Slv3Protocol.TxProductIdWch);
        Assert.Equal(0xE305, Slv3Protocol.RxProductIdWch);
        Assert.Equal(0x01, Slv3Protocol.WritePipeId);
        Assert.Equal(0x81, Slv3Protocol.ReadPipeId);
        Assert.Equal(64, Slv3Protocol.UsbPacketSize);
        Assert.Equal(240, Slv3Protocol.RfPayloadSize);
        Assert.Equal(60, Slv3Protocol.RfChunkSize);
        Assert.Equal(6, Slv3Protocol.PwmFollowMotherboard);
    }

    // ── BuildUsbSendRf: 240 B payload -> 4 x 64 B frames ──

    [Fact]
    public void BuildUsbSendRf_fragments_240_into_four_frames()
    {
        var payload = new byte[Slv3Protocol.RfPayloadSize];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)i;

        var frames = Slv3Protocol.BuildUsbSendRf(channel: 8, rxType: 3, payload);

        Assert.Equal(4, frames.Length);
        for (var f = 0; f < 4; f++)
        {
            Assert.Equal(64, frames[f].Length);
            Assert.Equal(Slv3Protocol.UsbSendRf, frames[f][0]);
            Assert.Equal((byte)f, frames[f][1]);   // chunk seq
            Assert.Equal(8, frames[f][2]);          // channel
            Assert.Equal(3, frames[f][3]);          // rxType
        }
        // Frame 0 carries payload[0..59] at offset 4.
        Assert.Equal(0, frames[0][4]);
        Assert.Equal(59, frames[0][63]);
        // Frame 3 carries payload[180..239].
        Assert.Equal(180, frames[3][4]);
        Assert.Equal(239, frames[3][63]);
    }

    [Fact]
    public void BuildUsbSendRf_zero_pads_a_short_tail_chunk()
    {
        var payload = new byte[70]; // 60 + 10 -> 2 frames, second mostly zero
        Array.Fill(payload, (byte)0xAB);
        var frames = Slv3Protocol.BuildUsbSendRf(1, 1, payload);
        Assert.Equal(2, frames.Length);
        Assert.Equal(0xAB, frames[1][4 + 9]);   // last real byte
        Assert.Equal(0x00, frames[1][4 + 10]);  // padded
    }

    // ── GetMac ──

    [Fact]
    public void BuildGetMac_emits_cmd_and_channel()
    {
        var frame = Slv3Protocol.BuildGetMac(8);
        Assert.Equal(64, frame.Length);
        Assert.Equal(0x11, frame[0]);
        Assert.Equal(8, frame[1]);
    }

    [Fact]
    public void TryParseGetMac_reads_mac_timer_and_version()
    {
        var reply = new byte[64];
        reply[0] = 0x11;
        MasterMac.CopyTo(reply, 1);
        reply[7] = 0x00; reply[8] = 0x01; reply[9] = 0x02; reply[10] = 0x03; // timer BE
        reply[11] = 0x01; reply[12] = 0x2C;                                   // fw 300

        Assert.True(Slv3Protocol.TryParseGetMac(reply, out var mac, out var timer, out var fw));
        Assert.Equal(MasterMac, mac);
        Assert.Equal(0x00010203u, timer);
        Assert.Equal(300, fw);
    }

    [Fact]
    public void TryParseGetMac_rejects_wrong_echo()
    {
        var reply = new byte[64];
        reply[0] = 0x99;
        Assert.False(Slv3Protocol.TryParseGetMac(reply, out _, out _, out _));
    }

    // ── GetDev ──

    [Fact]
    public void BuildGetDev_emits_cmd_and_page_count()
    {
        var frame = Slv3Protocol.BuildGetDev(2);
        Assert.Equal(0x10, frame[0]);
        Assert.Equal(2, frame[1]);
    }

    // ── RF header + Bind (PWM) ──

    [Fact]
    public void WriteRfHeader_lays_out_bytes_0_to_17()
    {
        var buf = new byte[Slv3Protocol.RfPayloadSize];
        Slv3Protocol.WriteRfHeader(buf, Slv3Protocol.RfRgbSync, FanMac, MasterMac,
            targetRx: 3, targetChannel: 8, slot: 1, cmdSeq: 5);

        Assert.Equal(0x12, buf[0]);
        Assert.Equal(0x20, buf[1]);
        Assert.Equal(FanMac, buf.AsSpan(2, 6).ToArray());
        Assert.Equal(MasterMac, buf.AsSpan(8, 6).ToArray());
        Assert.Equal(3, buf[14]);
        Assert.Equal(8, buf[15]);
        Assert.Equal(1, buf[16]);
        Assert.Equal(5, buf[17]);
    }

    [Fact]
    public void BuildBind_carries_pwm_tuple_at_17()
    {
        byte[] pwm = { 50, 75, 0, 6 };
        var payload = Slv3Protocol.BuildBind(FanMac, MasterMac, targetRx: 2, targetChannel: 8, slot: 1, pwm);

        Assert.Equal(Slv3Protocol.RfPayloadSize, payload.Length);
        Assert.Equal(0x12, payload[0]);
        Assert.Equal(0x10, payload[1]);         // RF_Bind
        Assert.Equal(2, payload[14]);
        Assert.Equal(1, payload[16]);           // slot (non-zero = bound)
        Assert.Equal(50, payload[17]);
        Assert.Equal(75, payload[18]);
        Assert.Equal(0, payload[19]);
        Assert.Equal(6, payload[20]);           // mobo-sync sentinel preserved
    }

    [Fact]
    public void EncodeDuty_maps_literal_six_to_zero_and_clamps()
    {
        Assert.Equal(0, Slv3Protocol.EncodeDuty(6));    // 6 is the mobo-sync sentinel
        Assert.Equal(0, Slv3Protocol.EncodeDuty(-5));
        Assert.Equal(100, Slv3Protocol.EncodeDuty(150));
        Assert.Equal(55, Slv3Protocol.EncodeDuty(55));
    }

    // ── Device record parsing ──

    [Fact]
    public void TryParseRecord_decodes_mac_type_rpm_pwm()
    {
        var reply = new byte[Slv3Protocol.RecordHeaderLength + Slv3Protocol.RecordLength];
        reply[0] = 0x10; reply[1] = 1;
        var rec = reply.AsSpan(Slv3Protocol.RecordHeaderLength);
        FanMac.CopyTo(rec.Slice(0));
        MasterMac.CopyTo(rec.Slice(6));
        rec[12] = 8;    // channel
        rec[13] = 3;    // rxType
        rec[18] = 0;    // dev_type: a wireless fan chain reports 0 (real Y70 value)
        rec[19] = 3;    // fan_num
        rec[24] = 0x18; rec[25] = 0x18; rec[26] = 0x18; // fans_type: 3x SLV3-LCD (24)
        // fans_speed @28: port0 RPM 0x0ABC (hi nibble masked), flags in the top nibble.
        rec[28] = 0xFA; rec[29] = 0xBC;   // -> ((0x0A)<<8)|0xBC = 0x0ABC = 2748
        rec[36] = 55;   // pwm port0
        rec[41] = 0x1C; // validator

        Assert.True(Slv3Protocol.TryParseRecord(reply, Slv3Protocol.RecordHeaderLength, out var record));
        Assert.Equal(FanMac, record.Mac);
        Assert.Equal(MasterMac, record.MasterMac);
        Assert.Equal(0, record.DevType);
        Assert.True(record.IsWirelessFan);     // non-master => a fan, regardless of dev_type 0
        Assert.False(record.IsMaster);
        Assert.Equal(24, record.PrimaryFanType); // SLV3-LCD from fans_type
        Assert.Equal(3, record.FanCount);
        Assert.Equal(0x0ABC, record.Rpm[0]);   // hi nibble flags masked off
        Assert.Equal(55, record.Pwm[0]);
    }

    [Fact]
    public void TryParseRecord_treats_devtype_FF_as_master_not_fan()
    {
        var reply = new byte[Slv3Protocol.RecordHeaderLength + Slv3Protocol.RecordLength];
        var rec = reply.AsSpan(Slv3Protocol.RecordHeaderLength);
        rec[18] = 0xFF; // the dongle's own record
        rec[41] = 0x1C;
        Assert.True(Slv3Protocol.TryParseRecord(reply, Slv3Protocol.RecordHeaderLength, out var record));
        Assert.True(record.IsMaster);
        Assert.False(record.IsWirelessFan);
    }

    [Fact]
    public void TryParseRecord_rejects_bad_validator()
    {
        var reply = new byte[Slv3Protocol.RecordHeaderLength + Slv3Protocol.RecordLength];
        reply[Slv3Protocol.RecordHeaderLength + 41] = 0x00; // not 0x1C
        Assert.False(Slv3Protocol.TryParseRecord(reply, Slv3Protocol.RecordHeaderLength, out _));
    }

    [Fact]
    public void TryParseRecord_forces_full_duty_when_spinning_but_zero_pwm()
    {
        var reply = new byte[Slv3Protocol.RecordHeaderLength + Slv3Protocol.RecordLength];
        var rec = reply.AsSpan(Slv3Protocol.RecordHeaderLength);
        rec[18] = 20;
        rec[28] = 0x05; rec[29] = 0x00; // port0 spinning (RPM 0x500)
        // all pwm bytes left 0
        rec[41] = 0x1C;
        Assert.True(Slv3Protocol.TryParseRecord(reply, Slv3Protocol.RecordHeaderLength, out var record));
        Assert.Equal(100, record.Pwm[0]);
    }

    [Fact]
    public void MacEquals_and_MacIsZero()
    {
        Assert.True(Slv3Protocol.MacEquals(FanMac, FanMac));
        Assert.False(Slv3Protocol.MacEquals(FanMac, MasterMac));
        Assert.True(Slv3Protocol.MacIsZero(new byte[6]));
        Assert.False(Slv3Protocol.MacIsZero(FanMac));
    }
}
