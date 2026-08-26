using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>Daemon verbosity override: only -v and -vv raise its stdout, and an
/// argument it rejects makes it print help rather than serve.</summary>
public class OpenRgbVerbosityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_AddsNoFlag(string? configured)
    {
        Assert.Null(OpenRgbProcessManager.ResolveVerbosityFlag(configured));
    }

    [Theory]
    [InlineData("verbose")]
    [InlineData("v")]
    [InlineData("  VERBOSE  ")]
    public void Verbose_MapsToSingleFlag(string configured)
    {
        Assert.Equal("-v", OpenRgbProcessManager.ResolveVerbosityFlag(configured));
    }

    [Theory]
    [InlineData("trace")]
    [InlineData("vv")]
    [InlineData("Trace")]
    public void Trace_MapsToDoubleFlag(string configured)
    {
        Assert.Equal("-vv", OpenRgbProcessManager.ResolveVerbosityFlag(configured));
    }

    [Fact]
    public void Debug_MapsToDoubleFlag()
    {
        // No flag targets LL_DEBUG on its own, and -v stops one level short of it.
        Assert.Equal("-vv", OpenRgbProcessManager.ResolveVerbosityFlag("debug"));
    }

    [Theory]
    [InlineData("error")]
    [InlineData("6")]
    [InlineData("-vvv")]
    [InlineData("--server")]
    public void UnrecognizedValues_AddNoFlag(string configured)
    {
        Assert.Null(OpenRgbProcessManager.ResolveVerbosityFlag(configured));
    }

    [Fact]
    public void ParsesTheDaemonVersionBanner()
    {
        // Captured verbatim from a bundled headless build's --version.
        const string banner =
            "OpenRGB 0.9+ (git), for controlling RGB lighting.\n"
            + "  Version:\t\t 0.9+ (git)\n"
            + "  Build Date\t\t Mon, 22 Jun 2026 20:11:54 -0700\n"
            + "  Git Commit ID\t\t a13b9393c7d3f46b4efa99ab7afeee96a61b9336\n"
            + "  Git Commit Date\t 2026-06-12 07:18:00 -0700\n"
            + "  Git Branch\t\t headless\n";

        Assert.Equal("0.9+ (git)", OpenRgbProcessManager.ParseVersionField(banner, "Version:"));
        Assert.Equal("a13b9393c7d3f46b4efa99ab7afeee96a61b9336", OpenRgbProcessManager.ParseVersionField(banner, "Git Commit ID"));
        Assert.Equal("headless", OpenRgbProcessManager.ParseVersionField(banner, "Git Branch"));
    }

    [Fact]
    public void ReportsUnknownWhenTheBannerIsMissingAField()
    {
        Assert.Equal("unknown", OpenRgbProcessManager.ParseVersionField("", "Git Commit ID"));
        Assert.Equal("unknown", OpenRgbProcessManager.ParseVersionField("Git Commit ID\t\n", "Git Commit ID"));
    }
}
