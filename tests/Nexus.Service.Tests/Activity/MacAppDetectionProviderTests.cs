using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

/// <summary>
/// Parse is fed captured `lsappinfo list` output, so the shape is pinned
/// independently of the host OS.
/// </summary>
public class MacAppDetectionProviderTests
{
    private const string Sample = """
 1) "loginwindow" ASN:0x0-0x2002: (in front)
    bundleID="com.apple.loginwindow"
    bundle path="/System/Library/CoreServices/loginwindow.app"
    pid = 171 type="UIElement" flavor=3 Version="3085.5.3" fileType="APPL" Arch=ARM64
 2) "Finder" ASN:0x0-0x3003:
    bundleID="com.apple.finder"
    pid = 640 type="Foreground" flavor=3 Version="1608" fileType="APPL" Arch=ARM64
 3) "cloudpaird" ASN:0x0-0x4004:
    pid = 700 type="BackgroundOnly" flavor=3 fileType="APPL" Arch=ARM64
61) "Calculator" ASN:0x0-0x184bf4a7:
    bundleID="com.apple.calculator"
    executable path="/System/Applications/Calculator.app/Contents/MacOS/Calculator"
    pid = 70090 type="Foreground" flavor=3 Version="225" fileType="APPL" Arch=ARM64 sandboxed
    launch time =  133 seconds ago, 2026/07/19 19:10:37 ( 2 minutes, 12.705 seconds ago )
""";

    [Fact]
    public void Parse_ReturnsOnlyForegroundGuiApps()
    {
        var apps = MacAppDetectionProvider.Parse(Sample);

        Assert.Equal(new[] { "Finder", "Calculator" }, apps.ConvertAll(a => a.Name));
    }

    [Fact]
    public void Parse_TakesThePidFromTheEntryBody()
    {
        var apps = MacAppDetectionProvider.Parse(Sample);

        Assert.Equal("640", apps.Find(a => a.Name == "Finder")!.Id);
        Assert.Equal("70090", apps.Find(a => a.Name == "Calculator")!.Id);
    }

    [Fact]
    public void Parse_OfEmptyOutput_ReturnsNothing()
    {
        Assert.Empty(MacAppDetectionProvider.Parse(""));
    }

    [Fact]
    public void Parse_DeduplicatesByName()
    {
        const string duplicated = """
 1) "Safari" ASN:0x0-0x1:
    pid = 10 type="Foreground" Arch=ARM64
 2) "Safari" ASN:0x0-0x2:
    pid = 11 type="Foreground" Arch=ARM64
""";

        var app = Assert.Single(MacAppDetectionProvider.Parse(duplicated));
        Assert.Equal("10", app.Id);
    }
}
