using System;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3RgbFrameTests
{
    private static readonly byte[] FanMac = Convert.FromHexString("112233445566");
    private static readonly byte[] MasterMac = Convert.FromHexString("AABBCCDDEEFF");
    private static readonly byte[] EffectIndex = { 0x01, 0x02, 0x03, 0x04 };

    [Fact]
    public void BuildFrameBuffer_at_full_brightness_passes_color_through()
    {
        var leds = new[] { new RgbColor(100, 50, 10) };
        var buf = Slv3RgbFrame.BuildFrameBuffer(leds, 100);
        // 100 * 255 >> 8 = 99 (the bit-shift formula is not a perfect
        // identity even at full brightness), matching GetRgbData exactly.
        Assert.Equal(new byte[] { 99, 49, 9 }, buf);
    }

    [Fact]
    public void BuildFrameBuffer_at_zero_brightness_is_black()
    {
        var leds = new[] { new RgbColor(255, 255, 255) };
        var buf = Slv3RgbFrame.BuildFrameBuffer(leds, 0);
        Assert.Equal(new byte[] { 0, 0, 0 }, buf);
    }

    [Fact]
    public void BuildFrameBuffer_caps_sum_at_600()
    {
        var leds = new[] { new RgbColor(255, 255, 255) };
        var buf = Slv3RgbFrame.BuildFrameBuffer(leds, 100);
        Assert.True(buf[0] + buf[1] + buf[2] <= 600);
    }

    [Fact]
    public void BuildEffectIndex_is_big_endian()
    {
        var bytes = Slv3RgbFrame.BuildEffectIndex(0x01020304);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, bytes);
    }

    [Fact]
    public void BuildPackets_header_packet_carries_common_fields_and_metadata()
    {
        var raw = Slv3RgbFrame.BuildFrameBuffer(new[] { new RgbColor(10, 20, 30) }, 100);
        var compressed = TinyUz.Compress(raw);

        var packets = Slv3RgbFrame.BuildPackets(FanMac, MasterMac, EffectIndex, compressed, ledCount: 40, totalFrames: 1, intervalMs: 100);

        var header = packets[0];
        Assert.Equal(Slv3Protocol.RfPayloadSize, header.Length);
        Assert.Equal(Slv3Protocol.RfFrameType, header[0]);
        Assert.Equal(Slv3Protocol.RfRgbSync, header[1]);
        Assert.Equal(FanMac, header.AsSpan(2, 6).ToArray());
        Assert.Equal(MasterMac, header.AsSpan(8, 6).ToArray());
        Assert.Equal(EffectIndex, header.AsSpan(14, 4).ToArray());
        Assert.Equal(0, header[18]);
        Assert.Equal(packets.Length, header[19]);

        var complen = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
        Assert.Equal(compressed.Length, complen);
        var totalFrame = (header[25] << 8) | header[26];
        Assert.Equal(1, totalFrame);
        Assert.Equal(40, header[27]);
        var interval = (header[32] << 8) | header[33];
        Assert.Equal(100, interval);

        var firstChunkLen = Math.Min(Slv3RgbFrame.FirstPacketDataMax, compressed.Length);
        Assert.Equal(
            compressed.AsSpan(0, firstChunkLen).ToArray(),
            header.AsSpan(Slv3RgbFrame.FirstPacketDataOffset, firstChunkLen).ToArray());
    }

    [Fact]
    public void BuildPackets_reassembles_compressed_stream_across_all_parts()
    {
        var leds = new RgbColor[160]; // 4 fans * 40 LEDs
        for (var i = 0; i < leds.Length; i++)
        {
            leds[i] = new RgbColor((byte)(i % 256), (byte)((i * 3) % 256), (byte)((i * 7) % 256));
        }
        var raw = Slv3RgbFrame.BuildFrameBuffer(leds, 100);
        var compressed = TinyUz.Compress(raw);
        Assert.True(compressed.Length > Slv3RgbFrame.FirstPacketDataMax, "test needs a multi-packet payload");

        var packets = Slv3RgbFrame.BuildPackets(FanMac, MasterMac, EffectIndex, compressed, ledCount: 160, totalFrames: 1, intervalMs: 50);

        var collected = new System.Collections.Generic.List<byte>();
        for (var i = 0; i < packets.Length; i++)
        {
            var offset = i == 0 ? Slv3RgbFrame.FirstPacketDataOffset : 20;
            var max = i == 0 ? Slv3RgbFrame.FirstPacketDataMax : Slv3RgbFrame.DataPacketChunk;
            var remaining = compressed.Length - collected.Count;
            var take = Math.Min(max, Math.Max(0, remaining));
            collected.AddRange(packets[i].AsSpan(offset, take).ToArray());
        }
        Assert.Equal(compressed, collected.ToArray());
    }

    [Fact]
    public void BuildPackets_data_packet_index_and_total_are_correct()
    {
        var leds = new RgbColor[160];
        var raw = Slv3RgbFrame.BuildFrameBuffer(leds, 100);
        // All-black data still compresses to a tiny stream (literal-only), so
        // pad the ledCount claim up regardless; this test only checks framing.
        var compressed = TinyUz.Compress(raw);
        var packets = Slv3RgbFrame.BuildPackets(FanMac, MasterMac, EffectIndex, compressed, ledCount: 160, totalFrames: 1, intervalMs: 50);

        for (var i = 0; i < packets.Length; i++)
        {
            Assert.Equal(i, packets[i][18]);
            Assert.Equal(packets.Length, packets[i][19]);
        }
    }
}
