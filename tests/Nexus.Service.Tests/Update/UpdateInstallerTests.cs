using Nexus.Service.Update;
using Xunit;

namespace Nexus.Service.Tests.Update;

public sealed class UpdateInstallerTests
{
    // The OTA installer embeds the version in the staged .cmd / log paths and
    // the schtask name, so it validates the tag first. The validator used to
    // accept only "v" + digits/dots, which rejected the "-beta.N" semver
    // prerelease suffix - so no prerelease could ever install via OTA.
    [Theory]
    [InlineData("v3.0.1", true)]
    [InlineData("v80", true)]                  // legacy monotonic tag
    [InlineData("v0.0.0", true)]
    [InlineData("v3.0.1-beta.1", true)]        // semver prerelease (the regression)
    [InlineData("v3.0.10-beta.2", true)]
    [InlineData("v3.0.1-rc.2", true)]
    [InlineData("", false)]
    [InlineData("3.0.1", false)]               // missing "v" prefix
    [InlineData("v", false)]
    [InlineData("vbeta", false)]               // no digit
    [InlineData("v3.0.1/x", false)]            // path separator
    [InlineData("v3.0.1\\x", false)]           // path separator
    [InlineData("v3.0.1 1", false)]            // space
    [InlineData("v3.0.1;rm", false)]           // shell metacharacter
    public void IsValidVersionTag_accepts_semver_including_prerelease(string tag, bool expected)
    {
        Assert.Equal(expected, UpdateInstaller.IsValidVersionTag(tag));
    }
}
