using Nexus.Service.Models.Panel;
using Nexus.Service.Panel.Streams;
using Xunit;

namespace Nexus.Service.Tests.Panel.Streams;

public class D213ProfileTests
{
    [Fact]
    public void Default_fs_kind_is_fullscreen_monitor_surface()
    {
        var profile = D213Profiles.Default("d213-fs");

        Assert.Equal("d213-fs", profile.Kind);
        Assert.Equal(PanelSurfaces.Monitor, profile.Surface);
        Assert.Equal(1024, profile.CssWidth);
        Assert.Equal(600, profile.CssHeight);
        Assert.Equal(30, profile.Fps);
        Assert.Equal(3500, profile.BitrateKbps);
    }

    [Fact]
    public void Default_q60_kind_is_square_q60_surface()
    {
        var profile = D213Profiles.Default("d213-q60");

        Assert.Equal("d213-q60", profile.Kind);
        Assert.Equal(PanelSurfaces.Q60, profile.Surface);
        Assert.Equal(800, profile.CssWidth);
        Assert.Equal(800, profile.CssHeight);
    }

    [Fact]
    public void Default_unknown_kind_falls_back_to_fs()
    {
        var profile = D213Profiles.Default("something-unrecognized");

        Assert.Equal("d213-fs", profile.Kind);
        Assert.Equal(PanelSurfaces.Monitor, profile.Surface);
    }

    [Fact]
    public void Resolve_null_record_returns_fs_default()
    {
        var profile = D213Profiles.Resolve(null);

        Assert.Equal("d213-fs", profile.Kind);
        Assert.Equal(30, profile.Fps);
        Assert.Equal(3500, profile.BitrateKbps);
    }

    [Fact]
    public void Resolve_record_with_no_overrides_returns_kind_default()
    {
        var record = new StreamedPanelRecord { PanelDeviceId = "dev-1", ProfileKind = "d213-q60" };

        var profile = D213Profiles.Resolve(record);

        Assert.Equal("d213-q60", profile.Kind);
        Assert.Equal(PanelSurfaces.Q60, profile.Surface);
        Assert.Equal(30, profile.Fps);
        Assert.Equal(3500, profile.BitrateKbps);
    }

    [Fact]
    public void Resolve_record_swaps_profile_kind()
    {
        var record = new StreamedPanelRecord { PanelDeviceId = "dev-1", ProfileKind = "d213-q60" };

        var profile = D213Profiles.Resolve(record);

        Assert.Equal(PanelSurfaces.Q60, profile.Surface);
        Assert.Equal(800, profile.CssWidth);
        Assert.Equal(800, profile.CssHeight);
    }

    [Fact]
    public void Resolve_record_overrides_fps_only()
    {
        var record = new StreamedPanelRecord { PanelDeviceId = "dev-1", Fps = 30 };

        var profile = D213Profiles.Resolve(record);

        Assert.Equal(30, profile.Fps);
        Assert.Equal(3500, profile.BitrateKbps);
    }

    [Fact]
    public void Resolve_record_overrides_bitrate_only()
    {
        var record = new StreamedPanelRecord { PanelDeviceId = "dev-1", BitrateKbps = 3000 };

        var profile = D213Profiles.Resolve(record);

        Assert.Equal(30, profile.Fps);
        Assert.Equal(3000, profile.BitrateKbps);
    }

    [Fact]
    public void Resolve_record_overrides_both_fps_and_bitrate()
    {
        var record = new StreamedPanelRecord
        {
            PanelDeviceId = "dev-1",
            ProfileKind = "d213-q60",
            Fps = 24,
            BitrateKbps = 2500,
        };

        var profile = D213Profiles.Resolve(record);

        Assert.Equal(PanelSurfaces.Q60, profile.Surface);
        Assert.Equal(24, profile.Fps);
        Assert.Equal(2500, profile.BitrateKbps);
    }

    [Fact]
    public void Resolve_unknown_profile_kind_in_record_falls_back_to_fs()
    {
        var record = new StreamedPanelRecord { PanelDeviceId = "dev-1", ProfileKind = "not-a-real-kind" };

        var profile = D213Profiles.Resolve(record);

        Assert.Equal("d213-fs", profile.Kind);
        Assert.Equal(PanelSurfaces.Monitor, profile.Surface);
    }
}
