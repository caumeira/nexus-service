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

    // --- IsNewer: prerelease precedence (a prerelease ranks below its release) ---

    [Theory]
    [InlineData("v3.1.0", "v3.1.0-beta.3", true)]        // stable IS newer than its prerelease
    [InlineData("v3.1.0-beta.3", "v3.1.0", false)]       // prerelease is NOT newer than its release
    [InlineData("v3.1.0-beta.2", "v3.1.0-beta.1", true)] // later beta is newer
    [InlineData("v3.1.0-beta.1", "v3.1.0-beta.2", false)]
    [InlineData("v3.1.0-beta.10", "v3.1.0-beta.9", true)] // numeric identifier compare, not lexical
    [InlineData("v3.1.0-beta.1", "v3.1.0-beta.1", false)] // equal
    [InlineData("v3.2.0-beta.1", "v3.1.0", true)]         // higher base wins regardless of suffix
    [InlineData("v3.0.1-rc1", "v3.0.0", true)]            // higher base, suffix irrelevant
    [InlineData("v3.0.0-beta", "v3.0.0", false)]          // prerelease below its release
    public void IsNewer_applies_prerelease_precedence(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, VersionCompare.IsNewer(candidate, current));
    }

    // --- IsNewer: local-dev "-dev" stamp (parses as an ordinary semver
    // prerelease identifier; ranks below its own stable release like any
    // other prerelease, but ranks ABOVE a same-patch "-beta.N" because
    // neither identifier is numeric so precedence falls to an ordinal
    // string compare ('d' > 'b') rather than the beta chain's numeric one.
    // A local dev build therefore never sees "update available" for a
    // same-patch beta or the matching stable release; a real patch/minor/
    // major bump is still detected correctly. ---

    [Theory]
    [InlineData("v3.0.0", "v3.0.0-dev", true)]           // stable release outranks the dev stamp
    [InlineData("v3.0.0-dev", "v3.0.0", false)]           // dev stamp is not newer than its release
    [InlineData("v3.0.0-beta.8", "v3.0.0-dev", false)]    // same-patch beta does NOT outrank dev ('b' < 'd')
    [InlineData("v3.0.0-dev", "v3.0.0-beta.8", true)]     // dev outranks a same-patch beta the other way
    [InlineData("v3.0.1", "v3.0.0-dev", true)]            // a real patch bump is still newer
    [InlineData("v3.1.0", "v3.0.0-dev", true)]            // a real minor bump is still newer
    [InlineData("v4.0.0", "v3.0.0-dev", true)]            // a real major bump is still newer
    [InlineData("v3.0.0-dev", "v3.0.0-dev", false)]       // equal
    public void IsNewer_applies_dev_stamp_precedence(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, VersionCompare.IsNewer(candidate, current));
    }

    [Fact]
    public void IsPrerelease_treats_dev_stamp_as_a_prerelease()
    {
        Assert.True(VersionCompare.IsPrerelease("v3.0.0-dev"));
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
