using System;

namespace Qos.Service.Peripherals.Protocols.Razer;

/// <summary>
/// Razer 90-byte feature report format (derived from OpenRazer's razer_common.c).
///
/// <code>
///   byte 0 : status (0x00 on request, 0x02 on success reply)
///   byte 1 : transaction id (echoed by device)
///   byte 2 : remaining packets (big-endian hi)
///   byte 3 : remaining packets (big-endian lo)
///   byte 4 : protocol type (0x00)
///   byte 5 : data size (number of valid argument bytes)
///   byte 6 : command class
///   byte 7 : command id
///   byte 8..87 : arguments (80 bytes, zero-padded)
///   byte 88 : CRC (XOR of bytes 2..87)
///   byte 89 : reserved (0x00)
/// </code>
///
/// On the wire this is preceded by a 1-byte HID report ID (0x00) so the full
/// HID feature report buffer is 91 bytes.
/// </summary>
public sealed class RazerReport
{
    public const int HidFeatureSize = 91;   // 1 byte report id + 90 byte payload
    public const int PayloadSize = 90;

    public byte Status;
    public byte TransactionId;
    public ushort RemainingPackets;
    public byte ProtocolType;
    public byte DataSize;
    public byte CommandClass;
    public byte CommandId;
    public readonly byte[] Arguments = new byte[80];

    public static RazerReport Command(byte commandClass, byte commandId, byte dataSize, ReadOnlySpan<byte> args)
    {
        var r = new RazerReport
        {
            Status = 0x00,
            // Default transaction id 0x00 matches OpenRazer's get_razer_report. Device-specific
            // code (e.g. DeathAdder V2 Pro → 0x3F) must override this before sending.
            TransactionId = 0x00,
            RemainingPackets = 0,
            ProtocolType = 0,
            DataSize = dataSize,
            CommandClass = commandClass,
            CommandId = commandId,
        };
        // Silently truncate if caller supplies more than 80 argument bytes so the
        // behavior matches the TS buildFrame on the WebHID side — both are driven
        // by the same shared JSON spec and tests cross-verify bytes.
        var toCopy = args.Length > r.Arguments.Length ? r.Arguments.Length : args.Length;
        args.Slice(0, toCopy).CopyTo(r.Arguments);
        return r;
    }

    /// <summary>Writes the HID feature report layout: 1 byte report ID (0x00) + 90 byte payload.</summary>
    public byte[] ToHidFeatureBuffer()
    {
        var buf = new byte[HidFeatureSize];
        buf[0] = 0x00;  // HID report id
        buf[1] = Status;
        buf[2] = TransactionId;
        buf[3] = (byte)(RemainingPackets >> 8);
        buf[4] = (byte)(RemainingPackets & 0xFF);
        buf[5] = ProtocolType;
        buf[6] = DataSize;
        buf[7] = CommandClass;
        buf[8] = CommandId;
        Array.Copy(Arguments, 0, buf, 9, 80);
        buf[89] = ComputeCrc(buf);
        buf[90] = 0;
        return buf;
    }

    public static RazerReport? Parse(ReadOnlySpan<byte> hidBuf)
    {
        if (hidBuf.Length < HidFeatureSize)
        {
            return null;
        }
        var r = new RazerReport
        {
            Status = hidBuf[1],
            TransactionId = hidBuf[2],
            RemainingPackets = (ushort)((hidBuf[3] << 8) | hidBuf[4]),
            ProtocolType = hidBuf[5],
            DataSize = hidBuf[6],
            CommandClass = hidBuf[7],
            CommandId = hidBuf[8],
        };
        hidBuf.Slice(9, 80).CopyTo(r.Arguments);
        return r;
    }

    /// <summary>XOR checksum of payload bytes 2..87 (inclusive). `buf` is the 91-byte HID feature buffer.</summary>
    public static byte ComputeCrc(ReadOnlySpan<byte> buf)
    {
        byte crc = 0;
        // Payload bytes 2..87 in 0-indexed space, which are buf indices 3..88.
        for (var i = 3; i <= 88; i++)
        {
            crc ^= buf[i];
        }
        return crc;
    }
}
