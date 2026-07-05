using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxKanaliDataTests
{
    [Theory]
    // The panel and Kanali disagree on the suffix form of the same upload; all three
    // must reduce to the identical timestamp stem so a stem lookup matches across them.
    [InlineData("2026-04-25_17-43-33-040.mp4.h264_2240x1080", "2026-04-25_17-43-33-040")]
    [InlineData("2026-04-25_17-43-33-040.Mp4", "2026-04-25_17-43-33-040")]
    [InlineData("2026-04-25_17-43-33-040.mp4", "2026-04-25_17-43-33-040")]
    [InlineData("download_86.mp4.h264_2240x1080", "download_86")]
    [InlineData("Screen_Recording_2025-05-05_0936.MP4", "screen_recording_2025-05-05_0936")]
    public void StemKey_reduces_naming_variants_to_one_key(string name, string expected)
    {
        Assert.Equal(expected, TryxKanaliData.StemKey(name));
    }

    [Fact]
    public void StemKey_of_empty_is_empty()
    {
        Assert.Equal(string.Empty, TryxKanaliData.StemKey(""));
    }
}
