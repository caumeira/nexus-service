using Nexus.Service.Update;
using Xunit;

namespace Nexus.Service.Tests.Update;

public sealed class VersionCompareTests
{
    // --- IsNewer: basic semver ordering ---

    [Theory]
    [InlineData("v3.0.1", "v3.0.0", true)]
    [InlineData("v3.1.0", "v3.0.9", true)]
    [InlineData("v4.0.0", "v3.9.9", true)]
    [InlineData("v3.0.0", "v3.0.0", false)]
    [InlineData("v2.9.9", "v3.0.0", false)]
    [InlineData("v3.0.0", "v3.0.1", false)]
    [InlineData("v3.0.0", "v3.1.0", false)]
    [InlineData("v3.0.0", "v4.0.0", false)]
    public void IsNewer_returns_correct_result(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, VersionCompare.IsNewer(candidate, current));
    }

    // --- IsNewer: "v" prefix tolerance ---

    [Theory]
    [InlineData("3.0.1",  "v3.0.0", true)]
    [InlineData("V3.0.1", "v3.0.0", true)]
    [InlineData("v3.0.1", "3.0.0",  true)]
    public void IsNewer_tolerates_missing_or_uppercase_v(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, VersionCompare.IsNewer(candidate, current));
    }

    // --- IsNewer: pre-release suffix ignored ---

    [Theory]
    [InlineData("v3.0.1-rc1", "v3.0.0", true)]
    [InlineData("v3.0.0-beta", "v3.0.0", false)]
    public void IsNewer_ignores_prerelease_suffix(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, VersionCompare.IsNewer(candidate, current));
    }

    // --- IsNewer: garbage is never newer ---

    [Theory]
    [InlineData("", "v3.0.0")]
    [InlineData("garbage", "v3.0.0")]
    [InlineData("63", "v3.0.0")]
    [InlineData(null, "v3.0.0")]
    [InlineData("v3.0.0", "garbage")]
    [InlineData("v3.0.0", "")]
    [InlineData("v-1.0.0", "v3.0.0")]
    public void IsNewer_garbage_is_never_newer(string? candidate, string current)
    {
        Assert.False(VersionCompare.IsNewer(candidate ?? "", current));
    }

    // --- TryParseSemver: valid inputs ---

    [Theory]
    [InlineData("v3.0.0",  3, 0, 0)]
    [InlineData("V3.0.0",  3, 0, 0)]
    [InlineData("3.0.0",   3, 0, 0)]
    [InlineData("v3.1.2",  3, 1, 2)]
    [InlineData("v3.0.1-rc1", 3, 0, 1)]
    [InlineData("v0.0.0",  0, 0, 0)]
    public void TryParseSemver_valid_tags(string tag, int major, int minor, int patch)
    {
        Assert.True(VersionCompare.TryParseSemver(tag, out var v));
        Assert.Equal((major, minor, patch), v);
    }

    // --- TryParseSemver: invalid inputs ---

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("v")]
    [InlineData("v-1.0.0")]
    [InlineData("va.b.c")]
    public void TryParseSemver_invalid_tags(string tag)
    {
        Assert.False(VersionCompare.TryParseSemver(tag, out _));
    }
}
