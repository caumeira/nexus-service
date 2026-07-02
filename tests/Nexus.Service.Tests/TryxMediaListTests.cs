using System;
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
}
