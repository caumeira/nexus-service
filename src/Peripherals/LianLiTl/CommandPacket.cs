using System;

namespace Nexus.Service.Peripherals.LianLiTl;

// Wire format from L-Connect / FanControl.LianLi:
// [0x01, cmd, 0x00, pktNo_hi, pktNo_lo, payloadLen, payload...] zero-padded to 64 bytes.
// pktNo is always 0 for host-originated commands.
internal static class CommandPacket
{
    public const byte ReportId = 0x01;
    public const int Length = 64;

    private const int CommandIndex = 1;
    private const int PayloadLengthIndex = 5;
    public const int PayloadOffset = 6;

    // Maximum payload bytes that fit after the 6-byte header.
    public const int MaxPayload = Length - PayloadOffset;

    public static byte[] Build(byte command, params byte[] payload)
    {
        if (payload == null)
        {
            throw new ArgumentNullException(nameof(payload));
        }
        if (payload.Length > MaxPayload)
        {
            throw new ArgumentException("Payload exceeds packet capacity.", nameof(payload));
        }
        var packet = new byte[Length];
        packet[0] = ReportId;
        packet[CommandIndex] = command;
        packet[PayloadLengthIndex] = (byte)payload.Length;
        Array.Copy(payload, 0, packet, PayloadOffset, payload.Length);
        return packet;
    }

    public static byte CommandOf(ReadOnlySpan<byte> packet) => packet[CommandIndex];

    public static int PayloadLengthOf(ReadOnlySpan<byte> packet) => packet[PayloadLengthIndex];
}
