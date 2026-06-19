using System;

namespace Nexus.Service.Lighting.Smart.Drivers.Govee;

/// <summary>
/// Binary "razer" (DreamView) packet packing. The packet goes base64-encoded
/// into the LAN API's <c>{"msg":{"cmd":"razer","data":{"pt":…}}}</c> command.
/// Layout: 0xBB magic, payload length (16-bit big-endian, bytes after the
/// command byte), command (0xB1 mode / 0xB0 colors), payload, then an XOR
/// checksum over every preceding byte including the magic.
/// </summary>
internal static class GoveePackets
{
    private const byte Magic = 0xBB;
    private const byte CmdMode = 0xB1;
    private const byte CmdColors = 0xB0;

    /// <summary>Enter/leave realtime mode. Devices revert to their built-in
    /// effect after ~1 minute without color frames, so streaming must keep
    /// flowing while an effect runs.</summary>
    public static byte[] BuildRazerMode(bool enable)
    {
        var buf = new byte[] { Magic, 0x00, 0x01, CmdMode, (byte)(enable ? 1 : 0), 0 };
        buf[^1] = Xor(buf.AsSpan(0, buf.Length - 1));
        return buf;
    }

    public static string RazerModeBase64(bool enable) => Convert.ToBase64String(BuildRazerMode(enable));

    /// <summary>One realtime color frame: the device spreads
    /// <paramref name="count"/> colors over the strip - when count equals the
    /// device's IC/segment count this is true per-segment control. RGB triplets
    /// are scaled by <paramref name="scale01"/> (brightness baked into color).
    /// <paramref name="gradient"/> = device blends between colors instead of
    /// hard per-segment blocks.</summary>
    public static byte[] BuildRazerFrame(ReadOnlySpan<byte> rgb, int count, float scale01, bool gradient)
    {
        count = Math.Clamp(count, 1, Math.Min(255, rgb.Length / 3));
        var payloadLen = 2 + 3 * count;
        var buf = new byte[4 + payloadLen + 1];
        buf[0] = Magic;
        buf[1] = (byte)(payloadLen >> 8);
        buf[2] = (byte)payloadLen;
        buf[3] = CmdColors;
        buf[4] = (byte)(gradient ? 1 : 0);
        buf[5] = (byte)count;
        var k = Math.Clamp(scale01, 0f, 1f);
        for (var i = 0; i < count * 3; i++)
            buf[6 + i] = (byte)Math.Clamp((int)Math.Round(rgb[i] * k), 0, 255);
        buf[^1] = Xor(buf.AsSpan(0, buf.Length - 1));
        return buf;
    }

    public static string RazerFrameBase64(ReadOnlySpan<byte> rgb, int count, float scale01, bool gradient)
        => Convert.ToBase64String(BuildRazerFrame(rgb, count, scale01, gradient));

    private static byte Xor(ReadOnlySpan<byte> bytes)
    {
        byte sum = 0;
        foreach (var b in bytes) sum ^= b;
        return sum;
    }
}
