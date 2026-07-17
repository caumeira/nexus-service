using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class ProcessActionGuardsTests
{
    [Theory]
    [InlineData("Nexus")]
    [InlineData("nexus")]
    [InlineData("nexus-overlay")]
    [InlineData("OpenRGB-headless")]
    [InlineData("openrgb-headless")]
    [InlineData("csrss")]
    [InlineData("wininit")]
    [InlineData("winlogon")]
    [InlineData("services")]
    [InlineData("lsass")]
    [InlineData("smss")]
    [InlineData("svchost")]
    [InlineData("dwm")]
    [InlineData("System")]
    [InlineData("Registry")]
    public void IsDenylisted_RefusesTheCriticalSet_CaseInsensitively(string name)
    {
        Assert.True(ProcessActionGuards.IsDenylisted(name));
    }

    [Theory]
    [InlineData("explorer")]
    [InlineData("chrome")]
    [InlineData("steam")]
    [InlineData("")]
    public void IsDenylisted_AllowsEverythingElse(string name)
    {
        Assert.False(ProcessActionGuards.IsDenylisted(name));
    }

    [Fact]
    public void IsDenylisted_TrimsSurroundingWhitespace_BeforeMatching()
    {
        Assert.True(ProcessActionGuards.IsDenylisted("  Nexus  "));
    }
}
