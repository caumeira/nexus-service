using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

/// <summary>
/// ResolveIconTargetPath is pure string work, so the bundle-resolution
/// contract is pinned independently of the host OS.
/// </summary>
public class MacProcessIconProviderTests
{
    [Theory]
    [InlineData("/Applications/Safari.app/Contents/MacOS/Safari", "/Applications/Safari.app")]
    [InlineData("/System/Applications/Utilities/Terminal.app/Contents/MacOS/Terminal", "/System/Applications/Utilities/Terminal.app")]
    public void ResolvesOwningBundle(string exePath, string expected)
    {
        Assert.Equal(expected, MacProcessIconProvider.ResolveIconTargetPath(exePath));
    }

    [Fact]
    public void NestedHelperBundleResolvesToOutermostApp()
    {
        const string helper = "/Applications/Google Chrome.app/Contents/Frameworks/Google Chrome Framework.framework/Versions/1.0/Helpers/Google Chrome Helper (Renderer).app/Contents/MacOS/Google Chrome Helper (Renderer)";
        Assert.Equal("/Applications/Google Chrome.app", MacProcessIconProvider.ResolveIconTargetPath(helper));
    }

    [Theory]
    [InlineData("/usr/bin/bash")]
    [InlineData("/opt/homebrew/bin/node")]
    [InlineData("")]
    public void NonBundlePathsPassThrough(string exePath)
    {
        Assert.Equal(exePath, MacProcessIconProvider.ResolveIconTargetPath(exePath));
    }

    [Fact]
    public void AppSuffixMustTerminateASegment()
    {
        const string lookalike = "/Users/x/My.app.backup/tool";
        Assert.Equal(lookalike, MacProcessIconProvider.ResolveIconTargetPath(lookalike));
    }

    [Fact]
    public void BundleMatchIsCaseInsensitive()
    {
        Assert.Equal(
            "/Applications/Foo.APP",
            MacProcessIconProvider.ResolveIconTargetPath("/Applications/Foo.APP/Contents/MacOS/foo"));
    }

    [Fact]
    public void BareDotAppSegmentIsNotABundle()
    {
        const string hidden = "/Users/x/.app/tool";
        Assert.Equal(hidden, MacProcessIconProvider.ResolveIconTargetPath(hidden));
    }

    [Fact]
    public void RelativeBundlePathResolves()
    {
        Assert.Equal("Nexus.app", MacProcessIconProvider.ResolveIconTargetPath("Nexus.app/Contents/MacOS/Nexus"));
    }
}
