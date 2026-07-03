using System;
using System.Collections.Generic;
using System.Text;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxMediaListTests
{
    private const string SampleMediaListPayload =
        "/userdata/default/default_01.mp4.h264_2240x1080\n" +
        "/userdata/default/default_02.mp4.h264_2240x1080\n" +
        "/userdata/default/default_03.mp4.h264_2240x1080\n" +
        "/userdata/default/default_04.mp4.h264_2240x1080\n" +
        "/userdata/default/default_05.mp4.h264_2240x1080\n" +
        "/userdata/default/default_06.mp4.h264_2240x1080\n" +
        "/userdata/default/start.mp4.h264_2240x1080\n" +
        "/userdata/default/screensaver.mp4.h264_2240x1080\n" +
        "/userdata/default/default_poweron.mp4.h264_2240x1080\n";

    [Fact]
    public void ParsePresetIds_returns_only_the_default_NN_wallpapers()
    {
        var data = Encoding.UTF8.GetBytes(SampleMediaListPayload);

        var ids = TryxMediaList.ParsePresetIds(data);

        Assert.Equal(
            new[] { "default_01", "default_02", "default_03", "default_04", "default_05", "default_06" },
            ids);
    }

    [Fact]
    public void ParsePresetIds_returns_empty_when_buffer_is_not_the_media_list()
    {
        var data = Encoding.UTF8.GetBytes("some unrelated heartbeat ack payload");

        var ids = TryxMediaList.ParsePresetIds(data);

        Assert.Empty(ids);
    }

    [Fact]
    public void ParsePresetIds_returns_empty_for_an_empty_buffer()
    {
        var ids = TryxMediaList.ParsePresetIds(ReadOnlySpan<byte>.Empty);

        Assert.Empty(ids);
    }

    [Fact]
    public void ParseMediaUsedBytes_sums_the_per_file_f3_sizes()
    {
        var blob = BuildMediaListBlob(
            ("/userdata/default/default_01.mp4.h264_2240x1080", 3790601),
            ("/userdata/default/default_06.mp4.h264_2240x1080", 12774063),
            ("/userdata/default/start.mp4.h264_2240x1080", 846687));

        Assert.Equal(3790601L + 12774063L + 846687L, TryxMediaList.ParseMediaUsedBytes(blob));
    }

    [Fact]
    public void ParseMediaUsedBytes_returns_null_when_not_a_media_list()
    {
        Assert.Null(TryxMediaList.ParseMediaUsedBytes(Encoding.UTF8.GetBytes("some heartbeat ack payload")));
        Assert.Null(TryxMediaList.ParseMediaUsedBytes(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ParseMediaUsedBytes_returns_null_for_the_marker_without_protobuf_sizes()
    {
        // The legacy text list carries the /userdata marker but no f503/f3 framing.
        Assert.Null(TryxMediaList.ParseMediaUsedBytes(Encoding.UTF8.GetBytes(SampleMediaListPayload)));
    }

    [Fact]
    public void ParseMediaUsedBytes_does_not_throw_on_a_corrupt_length()
    {
        // The panel's USB-FFS link is known to corrupt frames; a length varint with bit 31 set
        // (0x80000000) must not slip past the bounds check and throw in Slice - a throw would kill
        // the drain thread and re-arm the ~70s reset loop.
        var marker = Encoding.UTF8.GetBytes("/userdata/default/x");
        var inner = new List<byte>();
        WriteTag(inner, 2, 2);                                        // an entry (field 2, len-delimited)...
        inner.AddRange(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x08 });  // ...with a length claiming 0x80000000 bytes
        var b = new List<byte>();
        WriteTag(b, 1, 2); WriteVarint(b, (ulong)marker.Length); b.AddRange(marker);   // marker so the guard proceeds
        WriteTag(b, 503, 2); WriteVarint(b, (ulong)inner.Count); b.AddRange(inner);

        Assert.Null(TryxMediaList.ParseMediaUsedBytes(b.ToArray()));
    }

    // Encodes the panel's frame: top-level f1{f1:1}, empty f2, then f503 { repeated f2 {
    // f1:path, f2:ext, f3:sizeBytes, f4:1 } } - the shape ParseMediaUsedBytes walks.
    private static byte[] BuildMediaListBlob(params (string path, long size)[] files)
    {
        var entries = new List<byte>();
        foreach (var (path, size) in files)
        {
            var entry = new List<byte>();
            WriteTag(entry, 1, 2); WriteVarint(entry, (ulong)path.Length); entry.AddRange(Encoding.UTF8.GetBytes(path));
            WriteTag(entry, 2, 2); WriteVarint(entry, 3); entry.AddRange(Encoding.UTF8.GetBytes("mp4"));
            WriteTag(entry, 3, 0); WriteVarint(entry, (ulong)size);
            WriteTag(entry, 4, 0); WriteVarint(entry, 1);
            WriteTag(entries, 2, 2); WriteVarint(entries, (ulong)entry.Count); entries.AddRange(entry);
        }
        var top = new List<byte>();
        WriteTag(top, 1, 2); WriteVarint(top, 2); top.Add(0x08); top.Add(0x01);
        WriteTag(top, 2, 2); WriteVarint(top, 0);
        WriteTag(top, 503, 2); WriteVarint(top, (ulong)entries.Count); top.AddRange(entries);
        return top.ToArray();
    }

    private static void WriteTag(List<byte> b, int fieldNumber, int wireType)
        => WriteVarint(b, (ulong)((fieldNumber << 3) | wireType));

    private static void WriteVarint(List<byte> b, ulong v)
    {
        while (v >= 0x80) { b.Add((byte)((v & 0x7f) | 0x80)); v >>= 7; }
        b.Add((byte)v);
    }
}
