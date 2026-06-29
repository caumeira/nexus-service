using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLiCp;

namespace Nexus.Service.Peripherals.LianLiTl;

// Byte facts from L-Connect / FanControl.LianLi (TLFanDevice).
internal static class TlFanProtocol
{
    public const int VendorId = 0x0416;
    public const int ProductId = 0x7372;

    // Firmware accepts duty 12-100; 0% idles at wire value 1.
    public const int PwmMin = 12;
    public const int PwmMax = 100;
    public const byte PwmIdle = 1;

    // Timeout for the handshake reply (64-byte interrupt-IN response).
    public const int ReadTimeoutMs = 1000;

    private const byte CmdSetFanSpeed = 0xAA;
    private const byte CmdHandshake = 0xA1;
    private const byte CmdMotherboardSync = 0xB1;

    public static byte[] EncodeSetFanSpeed(int port, int fanIndex, int dutyPercent)
    {
        byte pwm = dutyPercent <= 0
            ? PwmIdle
            : (byte)Math.Clamp(dutyPercent, PwmMin, PwmMax);
        return CommandPacket.Build(CmdSetFanSpeed, Address(port, fanIndex), pwm);
    }

    public static byte[] EncodeHandshakeRequest() =>
        CommandPacket.Build(CmdHandshake);

    // High bit of the address byte enables mobo sync; host-control = sync off.
    public static byte[] EncodeMotherboardSync(int port, int fanIndex, bool sync)
    {
        byte address = (byte)((sync ? 0x80 : 0x00) | Address(port, fanIndex));
        return CommandPacket.Build(CmdMotherboardSync, address);
    }

    // Decodes the 3-byte records in a handshake reply payload.
    // No LINQ; safe to call in the per-poll path.
    public static List<TlFanReading> DecodeHandshake(ReadOnlySpan<byte> packet)
    {
        int payloadLen = CommandPacket.PayloadLengthOf(packet);
        int recordCount = payloadLen / 3;
        int payloadStart = CommandPacket.PayloadOffset;
        var readings = new List<TlFanReading>(recordCount);
        for (int i = 0; i < recordCount; i++)
        {
            int offset = payloadStart + i * 3;
            if (offset + 2 >= packet.Length)
            {
                break;
            }
            byte header = packet[offset];
            bool detected = (header & 0x80) != 0;
            if (!detected)
            {
                continue;
            }
            int port = (header >> 4) & 0x03;
            int fanIdx = header & 0x0F;
            int rpm = (packet[offset + 1] << 8) | packet[offset + 2];
            readings.Add(new TlFanReading(port, fanIdx, rpm));
        }
        return readings;
    }

    // Address byte: high nibble = port (0-3), low nibble = fanIdx.
    internal static byte Address(int port, int fanIndex) =>
        (byte)(((port & 0x0F) << 4) | (fanIndex & 0x0F));
}

public readonly struct TlFanReading
{
    public TlFanReading(int port, int fanIndex, int rpm)
    {
        Port = port;
        FanIndex = fanIndex;
        Rpm = rpm;
    }

    public int Port { get; }
    public int FanIndex { get; }
    public int Rpm { get; }
}
