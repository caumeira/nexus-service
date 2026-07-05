using System;
using Nexus.Service.Lifecycle;
using Xunit;

namespace Nexus.Service.Tests.Lifecycle;

public class PawnIoPathsTests
{
    // --- ShouldUseSystemDll ---

    [Fact]
    public void ShouldUseSystemDll_returns_false_when_system_version_is_unknown()
    {
        Assert.False(PawnIoPaths.ShouldUseSystemDll(null, new Version(2, 2, 0, 0)));
    }

    [Fact]
    public void ShouldUseSystemDll_returns_false_when_system_is_older()
    {
        Assert.False(PawnIoPaths.ShouldUseSystemDll(new Version(2, 1, 0, 0), new Version(2, 2, 0, 0)));
    }

    [Fact]
    public void ShouldUseSystemDll_returns_true_when_versions_are_equal()
    {
        Assert.True(PawnIoPaths.ShouldUseSystemDll(new Version(2, 2, 0, 0), new Version(2, 2, 0, 0)));
    }

    [Fact]
    public void ShouldUseSystemDll_returns_true_when_system_is_newer()
    {
        Assert.True(PawnIoPaths.ShouldUseSystemDll(new Version(2, 3, 0, 0), new Version(2, 2, 0, 0)));
    }

    [Theory]
    // Version.CompareTo treats an omitted build/revision as -1, lower than
    // any specified value, so "1.2" reads as older than "1.2.0.0".
    [InlineData("1.2", "1.2.0.0", false)]
    [InlineData("1.2.0.0", "1.2", true)]
    public void ShouldUseSystemDll_handles_versions_parsed_with_different_part_counts(
        string systemVersion, string bundledVersion, bool expected)
    {
        var system = Version.Parse(systemVersion);
        var bundled = Version.Parse(bundledVersion);

        Assert.Equal(expected, PawnIoPaths.ShouldUseSystemDll(system, bundled));
    }
}
