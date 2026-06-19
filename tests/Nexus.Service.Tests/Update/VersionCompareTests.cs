using Nexus.Service.Update;
using Xunit;

namespace Nexus.Service.Tests.Update;

public sealed class VersionCompareTests
{
    [Theory]
    [InlineData("v64", "v63", true)]
    [InlineData("v63", "v63", false)]
    [InlineData("v62", "v63", false)]
    [InlineData("v100", "v99", true)]
    [InlineData("v1", "v0", true)]
    public void IsNewer_returns_correct_result(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, VersionCompare.IsNewer(candidate, current));
    }

    [Theory]
    [InlineData("", "v63")]
    [InlineData("garbage", "v63")]
    [InlineData("63", "v63")]
    [InlineData(null, "v63")]
    [InlineData("v63", "garbage")]
    [InlineData("v63", "")]
    [InlineData("v-1", "v63")]
    public void IsNewer_garbage_is_never_newer(string? candidate, string current)
    {
        Assert.False(VersionCompare.IsNewer(candidate ?? "", current));
    }

    [Theory]
    [InlineData("v0", 0)]
    [InlineData("v1", 1)]
    [InlineData("v63", 63)]
    [InlineData("V63", 63)]
    public void TryParse_valid_tags(string tag, int expected)
    {
        Assert.True(VersionCompare.TryParse(tag, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("63")]
    [InlineData("garbage")]
    [InlineData("v")]
    [InlineData("v-1")]
    [InlineData("va")]
    public void TryParse_invalid_tags(string tag)
    {
        Assert.False(VersionCompare.TryParse(tag, out _));
    }
}
