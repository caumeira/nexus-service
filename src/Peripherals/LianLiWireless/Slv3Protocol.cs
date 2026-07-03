using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Pure byte-level builders/parsers for the Lian Li L-Wireless (SLV3) 2.4 GHz
/// dongle protocol. No IO; the transport owns the WinUSB endpoints. Facts are
/// from L-Connect 3 (lianli.slv3) and the public RE (sgtaziz/lian-li-linux,
/// phstudy/uni-wireless-sync). See plans/lianli-wireless-support.md.
///
/// Two framing layers:
///  - USB frame (64 B, to the TX/RX dongle over WinUSB EP 0x01, no report id):
///    [0]=USB_CMD [1]=arg/chunkSeq [2]=channel [3]=rxType [4..63]=60 B payload chunk.
///  - RF payload (240 B, what the fan firmware parses), fragmented into 4x60 B
///    chunks by Usb_SendRf: [0]=0x12 frameType [1]=RF_CMD [2..7]=fan MAC
///    [8..13]=master MAC [14]=targetRx [15]=targetChannel [16]=slot [17]=cmdSeq [18..]=body.
/// </summary>
public static class Slv3Protocol
{
    // WinUSB dongle ids. L-Connect matches either the Nuvoton pair or the WCH
    // alias for the same physical controller.
    public const int TxVendorId = 0x0416;
    public const int TxProductId = 0x8040;
    public const int RxVendorId = 0x0416;
    public const int RxProductId = 0x8041;
    public const int WchVendorId = 0x1A86;
    public const int TxProductIdWch = 0xE304;
    public const int RxProductIdWch = 0xE305;

    /// <summary>SetupDiGetClassDevs interface GUID for the TX/RX dongles (Y70 registry).</summary>
    public const string DongleInterfaceGuid = "{1D4B2365-4749-48EA-B38A-7C6FDDDD7E26}";

    // WinUSB endpoints (same on TX, RX, and each LCD): interrupt, 64-byte packets.
    public const byte WritePipeId = 0x01;
    public const byte ReadPipeId = 0x81;
    public const int UsbPacketSize = 64;
    public const int RfPayloadSize = 240;
    public const int RfChunkSize = 60;

    public const int MacLength = 6;

    // USB_CMD (first byte of a USB frame to a dongle).
    public const byte UsbSendRf = 0x10;   // transmit an RF payload via the TX; also GetDev on the RX
    public const byte UsbGetMac = 0x11;   // query own MAC + clock + fw (TX)
    public const byte UsbResetAnother = 0x15;

    // RF_CMD (payload byte 1). Values per the decompiled RFCmdType numbering.
    public const byte RfBind = 0x10;               // bind/unbind + carries the 4-byte PWM tuple
    public const byte RfSelect = 0x12;             // identify a fan
    public const byte RfClockSync = 0x14;          // master-clock heartbeat (broadcast)
    public const byte RfSaveCfg = 0x15;            // persist to fan flash
    public const byte RfRgbSync = 0x20;            // streamed RGB frame animation
    public const byte RfMbSyncSwitch = 0x24;
    public const byte RfLightSyncSwitch = 0x26;

    /// <summary>Constant frame-type byte at RF payload[0] for every host->fan frame.</summary>
    public const byte RfFrameType = 0x12;

    /// <summary>Default RF channel; user channels are odd (firmware rejects even).</summary>
    public const byte DefaultChannel = 8;

    /// <summary>rx_type slot ids a master hands out to bound fans.</summary>
    public const int MinSlot = 1;
    public const int MaxSlot = 14;

    // Device-list record (42 bytes, from the RX GetDev reply).
    public const int RecordLength = 42;
    public const int RecordHeaderLength = 4;   // [0]=cmd echo [1]=count [2..3]=ver/flags
    public const int PageLength = 434;
    public const byte RecordValidator = 0x1C;  // record[41] must equal this

    /// <summary>PWM byte meaning "follow motherboard PWM header". Real duties skip 6.</summary>
    public const byte PwmFollowMotherboard = 6;
    public const int PortsPerRecord = 4;

    // dev_type ranges that identify our wireless LCD fans in a device record.
    public const byte DevTypeSlv3Fan = 20;      // 20-23 SLV3 LED, 24-26 SLV3 LCD
    public const byte DevTypeSlInfinity = 36;   // 36-39 SL-Infinity

    /// <summary>
    /// Fragment a 240-byte RF payload into <see cref="UsbPacketSize"/>-byte USB
    /// frames for the TX: [0]=UsbSendRf [1]=chunkSeq [2]=channel [3]=rxType
    /// [4..]=60 payload bytes. Returns ceil(len/60) frames (4 for a 240 B payload).
    /// </summary>
    public static byte[][] BuildUsbSendRf(byte channel, byte rxType, ReadOnlySpan<byte> rfPayload)
    {
        var frames = (rfPayload.Length + RfChunkSize - 1) / RfChunkSize;
        var result = new byte[frames][];
        for (var i = 0; i < frames; i++)
        {
            var frame = new byte[UsbPacketSize];
            frame[0] = UsbSendRf;
            frame[1] = (byte)i;
            frame[2] = channel;
            frame[3] = rxType;
            var offset = i * RfChunkSize;
            var count = Math.Min(RfChunkSize, rfPayload.Length - offset);
            rfPayload.Slice(offset, count).CopyTo(frame.AsSpan(4));
            result[i] = frame;
        }
        return result;
    }

    /// <summary>USB frame that asks the TX for the master MAC/clock/fw: [0]=0x11 [1]=channel.</summary>
    public static byte[] BuildGetMac(byte channel)
    {
        var frame = new byte[UsbPacketSize];
        frame[0] = UsbGetMac;
        frame[1] = channel;
        return frame;
    }

    /// <summary>USB frame that asks the RX for <paramref name="pageCount"/> device-list pages: [0]=0x10 [1]=pageCount.</summary>
    public static byte[] BuildGetDev(byte pageCount)
    {
        var frame = new byte[UsbPacketSize];
        frame[0] = UsbSendRf;
        frame[1] = pageCount;
        return frame;
    }

    /// <summary>
    /// Parse the GetMac reply: [0]=0x11 echo, [1..6]=master MAC, [7..10]=RF timer
    /// (BE u32), [11..12]=TX fw version (BE). Returns false if the echo is wrong.
    /// </summary>
    public static bool TryParseGetMac(ReadOnlySpan<byte> reply, out byte[] masterMac, out uint rfTimer, out int fwVersion)
    {
        masterMac = Array.Empty<byte>();
        rfTimer = 0;
        fwVersion = 0;
        if (reply.Length < 13 || reply[0] != UsbGetMac) return false;
        masterMac = reply.Slice(1, MacLength).ToArray();
        rfTimer = (uint)((reply[7] << 24) | (reply[8] << 16) | (reply[9] << 8) | reply[10]);
        fwVersion = (reply[11] << 8) | reply[12];
        return true;
    }

    /// <summary>
    /// Common RF payload header shared by every host->fan frame. Writes bytes
    /// 0..17; the caller fills the command body from [18]. <paramref name="dst"/>
    /// must be at least <see cref="RfPayloadSize"/> bytes and is not cleared.
    /// </summary>
    public static void WriteRfHeader(
        Span<byte> dst, byte rfCmd, ReadOnlySpan<byte> fanMac, ReadOnlySpan<byte> masterMac,
        byte targetRx, byte targetChannel, byte slot, byte cmdSeq)
    {
        dst[0] = RfFrameType;
        dst[1] = rfCmd;
        fanMac.Slice(0, MacLength).CopyTo(dst.Slice(2));
        masterMac.Slice(0, MacLength).CopyTo(dst.Slice(8));
        dst[14] = targetRx;
        dst[15] = targetChannel;
        dst[16] = slot;
        dst[17] = cmdSeq;
    }

    /// <summary>
    /// Build the RF_Bind (0x10) payload. Doubles as the PWM keep-alive: [17..20]
    /// carry four raw duty percents (0..100; <see cref="PwmFollowMotherboard"/>=mobo
    /// sync). <paramref name="slot"/> 0 releases the fan (unbind). Unoccupied ports
    /// must stay 0.
    /// </summary>
    public static byte[] BuildBind(
        ReadOnlySpan<byte> fanMac, ReadOnlySpan<byte> masterMac,
        byte targetRx, byte targetChannel, byte slot, ReadOnlySpan<byte> pwm4)
    {
        var payload = new byte[RfPayloadSize];
        // Bind puts the PWM tuple where the generic header's [17] cmd_seq would be,
        // so write the header then overwrite [17..20] with the duties.
        WriteRfHeader(payload, RfBind, fanMac, masterMac, targetRx, targetChannel, slot, 0);
        for (var i = 0; i < PortsPerRecord && i < pwm4.Length; i++)
        {
            payload[17 + i] = pwm4[i];
        }
        return payload;
    }

    /// <summary>Encode a duty percent to a wire byte, mapping a literal 6 to 0 so it is not read as mobo-sync.</summary>
    public static byte EncodeDuty(int percent)
    {
        var d = Math.Clamp(percent, 0, 100);
        return d == PwmFollowMotherboard ? (byte)0 : (byte)d;
    }

    /// <summary>SLV3 minimum non-zero duty percent; lower requests would stall the fan.</summary>
    public const int MinDutyPercent = 14;

    /// <summary>Floors a nonzero duty percent up to <see cref="MinDutyPercent"/>; 0 (fully off) is left alone.</summary>
    public static int FloorDuty(int percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        return clamped is > 0 and < MinDutyPercent ? MinDutyPercent : clamped;
    }

    /// <summary>
    /// Wire byte for one bind-frame duty port: null follows the motherboard
    /// PWM header (<see cref="PwmFollowMotherboard"/>); otherwise a manual
    /// percent, floored via <see cref="FloorDuty"/> then encoded via
    /// <see cref="EncodeDuty"/> so it can never collide with the mobo-sync
    /// sentinel.
    /// </summary>
    public static byte ResolvePortDuty(int? percent) =>
        percent is null ? PwmFollowMotherboard : EncodeDuty(FloorDuty(percent.Value));

    /// <summary>
    /// Builds the 4-port duty tuple for a bind frame from per-port targets
    /// (null = motherboard-sync). Ports at or beyond <paramref name="fanCount"/>
    /// are unoccupied and stay 0 (plans/lianli-wireless-support.md section 3).
    /// </summary>
    public static byte[] BuildPwmTuple(IReadOnlyList<int?> targets, int fanCount)
    {
        var pwm = new byte[PortsPerRecord];
        for (var port = 0; port < PortsPerRecord && port < fanCount; port++)
        {
            pwm[port] = ResolvePortDuty(port < targets.Count ? targets[port] : null);
        }
        return pwm;
    }

    /// <summary>Number of valid records in a GetDev reply (first byte is the command echo, second is the count).</summary>
    public static int RecordCount(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 2 || reply[0] != UsbSendRf) return 0;
        return reply[1];
    }

    /// <summary>
    /// Parse one 42-byte device record at <paramref name="offset"/>. Returns false
    /// if out of range or the trailing validator byte is not 0x1C.
    /// </summary>
    public static bool TryParseRecord(ReadOnlySpan<byte> reply, int offset, out Slv3DeviceRecord record)
    {
        record = default;
        if (offset + RecordLength > reply.Length) return false;
        var rec = reply.Slice(offset, RecordLength);
        if (rec[41] != RecordValidator) return false;

        var mac = rec.Slice(0, MacLength).ToArray();
        var masterMac = rec.Slice(6, MacLength).ToArray();
        var channel = rec[12];
        var rxType = rec[13];
        var devType = rec[18];
        var fanNumRaw = rec[19];
        var rightAttach = fanNumRaw >= 10;
        var fanNum = rightAttach ? fanNumRaw - 10 : fanNumRaw;

        var effectIndex = new byte[4];
        rec.Slice(20, 4).CopyTo(effectIndex);

        // fans_type carries the per-port fan subtype (0x18=24 SLV3-LCD, 20-23
        // SLV3-LED, 36-39 SL-Infinity); dev_type at [18] is a coarse category and
        // reads 0 for a wireless fan chain, so the family lives here.
        var fansType = new byte[PortsPerRecord];
        rec.Slice(24, PortsPerRecord).CopyTo(fansType);

        var rpm = new int[PortsPerRecord];
        var pwm = new int[PortsPerRecord];
        var anyRpm = false;
        for (var k = 0; k < PortsPerRecord; k++)
        {
            // fans_speed is 8 bytes at [28]; the hi nibble of bytes 0/2/4/6 holds
            // flags and must be masked, leaving a big-endian 12-bit RPM per port.
            var hi = rec[28 + k * 2] & 0x0F;
            var lo = rec[28 + k * 2 + 1];
            rpm[k] = (hi << 8) | lo;
            if (rpm[k] > 0) anyRpm = true;
            pwm[k] = rec[36 + k];
        }
        // A spinning fan reporting zero duty is at firmware default (full).
        if (anyRpm && pwm[0] == 0 && pwm[1] == 0 && pwm[2] == 0 && pwm[3] == 0)
        {
            for (var k = 0; k < PortsPerRecord; k++) pwm[k] = 100;
        }

        record = new Slv3DeviceRecord(mac, masterMac, channel, rxType, devType, fanNum, rightAttach, effectIndex, fansType, rpm, pwm, rec[40]);
        return true;
    }

    /// <summary>True when both MACs are equal over their first 6 bytes.</summary>
    public static bool MacEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length < MacLength || b.Length < MacLength) return false;
        for (var i = 0; i < MacLength; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    /// <summary>True when the MAC is all zeros (an unbound device reports a zero master MAC).</summary>
    public static bool MacIsZero(ReadOnlySpan<byte> mac)
    {
        for (var i = 0; i < MacLength && i < mac.Length; i++)
        {
            if (mac[i] != 0) return false;
        }
        return true;
    }
}

/// <summary>One parsed 42-byte device-list record from the RX dongle.</summary>
public readonly record struct Slv3DeviceRecord(
    byte[] Mac,
    byte[] MasterMac,
    byte Channel,
    byte RxType,
    byte DevType,
    int FanCount,
    bool RightAttach,
    byte[] EffectIndex,
    byte[] FansType,
    int[] Rpm,
    int[] Pwm,
    byte CmdSeq)
{
    /// <summary>
    /// Any non-master record on the RF link (L-Connect's rfList rule: dev_type 0xFF
    /// is a master, everything else is a device). A wireless fan chain reports
    /// dev_type 0 with the fan subtype in <see cref="FansType"/>.
    /// </summary>
    public bool IsWirelessFan => DevType != 0xFF;

    /// <summary>A record with dev_type 0xFF is another master on the link, not a fan.</summary>
    public bool IsMaster => DevType == 0xFF;

    /// <summary>Per-port fan subtype (0x18=24 SLV3-LCD, 20-23 SLV3-LED, 36-39 SL-Infinity); 0 if no fan on that port.</summary>
    public byte PrimaryFanType => FansType.Length > 0 ? FansType[0] : (byte)0;
}
