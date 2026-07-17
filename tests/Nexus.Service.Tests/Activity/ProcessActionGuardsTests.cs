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

    // ShouldRefuseKill is the exact gate the helper's process.kill handler
    // runs before ever calling ProcessKiller.KillAllCounted (see
    // ProcessActionsDomain.cs) - the helper cannot be exercised directly
    // from a portable test (it depends on Windows-only helper-pipe types),
    // so this pins the shared decision it delegates to instead.
    [Theory]
    [InlineData("Nexus")]
    [InlineData("lsass")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShouldRefuseKill_RefusesMissingOrDenylistedNames(string? name)
    {
        Assert.True(ProcessActionGuards.ShouldRefuseKill(name));
    }

    [Fact]
    public void ShouldRefuseKill_AllowsAnOrdinaryName()
    {
        Assert.False(ProcessActionGuards.ShouldRefuseKill("chrome"));
    }
}
