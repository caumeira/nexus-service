using Nexus.Service.Update;
using Xunit;

namespace Nexus.Service.Tests.Update;

/// <summary>
/// Tests for the channel-follows-build logic in UpdateService.ResolveChannelForBuild.
/// </summary>
public sealed class ChannelFollowsBuildTests
{
    // --- Beta build: channel becomes "beta" ---

    [Theory]
    [InlineData("v3.1.0-beta.1")]
    [InlineData("v3.1.0-beta.10")]
    [InlineData("v3.0.0-rc.1")]
    [InlineData("v3.0.0-rc2")]
    public void Beta_build_sets_channel_beta(string buildVersion)
    {
        var (channel, changed) = UpdateService.ResolveChannelForBuild("", buildVersion, "production");
        Assert.Equal("beta", channel);
        Assert.True(changed);
    }

    // --- Stable build: channel becomes "production" ---

    [Theory]
    [InlineData("v3.1.0")]
    [InlineData("v3.0.0")]
    [InlineData("v4.2.1")]
    public void Stable_build_sets_channel_production(string buildVersion)
    {
        var (channel, changed) = UpdateService.ResolveChannelForBuild("", buildVersion, "beta");
        Assert.Equal("production", channel);
        Assert.True(changed);
    }

    // --- Fresh install (LastRunVersion empty): channel derived from build ---

    [Fact]
    public void Fresh_install_beta_build_sets_beta()
    {
        var (channel, changed) = UpdateService.ResolveChannelForBuild("", "v3.1.0-beta.1", "production");
        Assert.Equal("beta", channel);
        Assert.True(changed);
    }

    [Fact]
    public void Fresh_install_stable_build_sets_production()
    {
        var (channel, changed) = UpdateService.ResolveChannelForBuild("", "v3.0.0", "beta");
        Assert.Equal("production", channel);
        Assert.True(changed);
    }

    // --- Same-version restart: channel left unchanged ---

    [Fact]
    public void Same_version_restart_preserves_manual_beta_choice()
    {
        var (channel, changed) = UpdateService.ResolveChannelForBuild("v3.0.0", "v3.0.0", "beta");
        Assert.Equal("beta", channel);
        Assert.False(changed);
    }

    [Fact]
    public void Same_version_restart_preserves_manual_production_choice()
    {
        var (channel, changed) = UpdateService.ResolveChannelForBuild("v3.1.0-beta.1", "v3.1.0-beta.1", "production");
        Assert.Equal("production", channel);
        Assert.False(changed);
    }

    // --- Version changed: settings are written ---

    [Fact]
    public void OTA_update_stable_to_stable_resets_to_production()
    {
        var (channel, changed) = UpdateService.ResolveChannelForBuild("v3.0.0", "v3.1.0", "beta");
        Assert.Equal("production", channel);
        Assert.True(changed);
    }

    [Fact]
    public void OTA_update_stable_to_beta_switches_to_beta()
    {
        var (channel, changed) = UpdateService.ResolveChannelForBuild("v3.0.0", "v3.1.0-beta.1", "production");
        Assert.Equal("beta", channel);
        Assert.True(changed);
    }

    [Fact]
    public void OTA_update_beta_to_stable_switches_to_production()
    {
        var (channel, changed) = UpdateService.ResolveChannelForBuild("v3.1.0-beta.1", "v3.1.0", "beta");
        Assert.Equal("production", channel);
        Assert.True(changed);
    }

    // --- IsPrerelease helper via ResolveChannelForBuild ---

    [Theory]
    [InlineData("v3.0.0", false)]
    [InlineData("v3.0.0-beta.1", true)]
    [InlineData("v3.0.0-rc.1", true)]
    [InlineData("garbage", false)]
    [InlineData("", false)]
    public void IsPrerelease_matches_expected(string version, bool expected)
    {
        Assert.Equal(expected, VersionCompare.IsPrerelease(version));
    }
}
