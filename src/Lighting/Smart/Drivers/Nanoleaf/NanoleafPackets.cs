using System;

namespace Nexus.Service.Lighting.Smart.Drivers.Nanoleaf;

/// <summary>
/// External Control v2 datagram packing (UDP to the controller). All
/// multi-byte fields big-endian: zone count, then per zone: zoneId, R, G, B,
/// W (always 0 - white is the device's own calibration), transition in 100 ms
/// units. Zone id is the panelId on panel products and the LED index on
/// Essentials strips.
/// </summary>
internal static class NanoleafPackets
{
    /// <summary>Zones per datagram cap that keeps each packet within a single
    /// MTU (long Essentials strips are chunked; unaddressed zones keep their
    /// previous color, so chunking is lossless).</summary>
    public const int MaxZonesPerDatagram = 120;

    /// <summary>Build one v2 datagram for zones [start, start+count). Empty
    /// <paramref name="zoneIds"/> means identity ids (LED-index addressing).
    /// RGB triplets are scaled by <paramref name="scale01"/> (the brightness -
    /// the device's own brightness state is pinned to 100 while streaming).</summary>
    public static byte[] BuildV2Datagram(
        ReadOnlySpan<int> zoneIds, int start, int count,
        ReadOnlySpan<byte> rgb, float scale01, int transitionTenths)
    {
        var buf = new byte[2 + count * 8];
        buf[0] = (byte)(count >> 8);
        buf[1] = (byte)count;
        var off = 2;
        for (var i = 0; i < count; i++)
        {
            var zone = start + i;
            var id = zoneIds.IsEmpty ? zone : zoneIds[zone];
            var src = zone * 3;
            buf[off] = (byte)(id >> 8);
            buf[off + 1] = (byte)id;
            buf[off + 2] = Scale(rgb[src], scale01);
            buf[off + 3] = Scale(rgb[src + 1], scale01);
            buf[off + 4] = Scale(rgb[src + 2], scale01);
            buf[off + 5] = 0;
            buf[off + 6] = (byte)(transitionTenths >> 8);
            buf[off + 7] = (byte)transitionTenths;
            off += 8;
        }
        return buf;
    }

    private static byte Scale(byte c, float k)
        => (byte)Math.Clamp((int)Math.Round(c * Math.Clamp(k, 0f, 1f)), 0, 255);
}
