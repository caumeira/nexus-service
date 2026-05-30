using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>Covers the wpctl/pactl output parsing (the part that runs without spawning a subprocess).</summary>
public class LinuxVolumeProviderTests
{
    [Fact]
    public void ParseWpctl_ReadsVolume()
    {
        var s = LinuxVolumeProvider.ParseWpctl("Volume: 0.65\n");
        Assert.True(s.Supported);
        Assert.Equal(0.65, s.Volume, 3);
        Assert.False(s.Muted);
    }

    [Fact]
    public void ParseWpctl_DetectsMuted()
    {
        var s = LinuxVolumeProvider.ParseWpctl("Volume: 0.40 [MUTED]");
        Assert.True(s.Muted);
        Assert.Equal(0.40, s.Volume, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no volume here")]
    public void ParseWpctl_GarbageIsUnsupported(string output)
        => Assert.False(LinuxVolumeProvider.ParseWpctl(output).Supported);

    [Fact]
    public void ParsePactl_ReadsPercentAndMute()
    {
        const string volOut = "Volume: front-left: 42598 /  65% / -9.30 dB,   front-right: 42598 / 65% / -9.30 dB";
        var s = LinuxVolumeProvider.ParsePactl(volOut, "Mute: yes");
        Assert.True(s.Supported);
        Assert.Equal(0.65, s.Volume, 3);
        Assert.True(s.Muted);
        Assert.False(LinuxVolumeProvider.ParsePactl(volOut, "Mute: no").Muted);
    }

    [Fact]
    public void ParsePactl_NoPercentIsUnsupported()
        => Assert.False(LinuxVolumeProvider.ParsePactl("Volume: unknown", "Mute: no").Supported);
}
