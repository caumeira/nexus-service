using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// The stored route is replayed into a reopening window, so anything that is
/// not an absolute same-origin path must be dropped rather than stored.
/// </summary>
public class SessionRoutesTests
{
    [Theory]
    [InlineData("/system/monitoring/cpu")]
    [InlineData("/")]
    [InlineData("/system/diagnostics/cooling")]
    public void Sanitize_KeepsAnAbsoluteSameOriginPath(string path)
    {
        Assert.Equal(path, SessionRoutes.Sanitize(path));
    }

    [Theory]
    [InlineData("//evil.example.com/x")]
    [InlineData("/\\evil.example.com/x")]
    [InlineData("https://evil.example.com/x")]
    [InlineData("system/monitoring")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Sanitize_DropsAnythingThatCouldLeaveTheOrigin(string? path)
    {
        Assert.Equal("", SessionRoutes.Sanitize(path));
    }

    [Fact]
    public void Sanitize_DropsControlCharactersAndOverlongValues()
    {
        Assert.Equal("", SessionRoutes.Sanitize("/system/\nmonitoring"));
        Assert.Equal("", SessionRoutes.Sanitize("/" + new string('a', 300)));
    }

    [Fact]
    public void Sanitize_TrimsSurroundingWhitespace()
    {
        Assert.Equal("/system/settings", SessionRoutes.Sanitize("  /system/settings  "));
    }
}
