using System;
using Nexus.Service.Lifecycle;
using Xunit;

namespace Nexus.Service.Tests.Lifecycle;

public class PawnIoInstallerTests
{
    // --- ShouldUpgrade ---

    [Fact]
    public void ShouldUpgrade_returns_false_when_installed_version_is_unknown()
    {
        Assert.False(PawnIoInstaller.ShouldUpgrade(null, new Version(2, 2, 0, 0)));
    }

    [Fact]
    public void ShouldUpgrade_returns_true_when_installed_is_older()
    {
        Assert.True(PawnIoInstaller.ShouldUpgrade(new Version(2, 1, 0, 0), new Version(2, 2, 0, 0)));
    }

    [Fact]
    public void ShouldUpgrade_returns_false_when_versions_are_equal()
    {
        Assert.False(PawnIoInstaller.ShouldUpgrade(new Version(2, 2, 0, 0), new Version(2, 2, 0, 0)));
    }

    [Fact]
    public void ShouldUpgrade_never_downgrades_when_installed_is_newer()
    {
        Assert.False(PawnIoInstaller.ShouldUpgrade(new Version(2, 3, 0, 0), new Version(2, 2, 0, 0)));
    }

    [Theory]
    [InlineData("2.2.0", "2.2.0.1", true)]    // bundled adds a revision-part bump
    [InlineData("2.2.0.1", "2.2.0", false)]   // installed already has it, bundled reads as older
    [InlineData("2.1", "2.2.0.0", true)]      // installed omits build/revision entirely, minor differs
    public void ShouldUpgrade_handles_versions_parsed_with_different_part_counts(
        string installedVersion, string bundledVersion, bool expected)
    {
        var installed = Version.Parse(installedVersion);
        var bundled = Version.Parse(bundledVersion);

        Assert.Equal(expected, PawnIoInstaller.ShouldUpgrade(installed, bundled));
    }

    // --- ResolveImagePath ---

    [Fact]
    public void ResolveImagePath_leaves_an_absolute_drive_path_unchanged()
    {
        var resolved = PawnIoInstaller.ResolveImagePath(
            @"C:\Windows\System32\drivers\PawnIO.sys", @"C:\Windows");

        Assert.Equal(@"C:\Windows\System32\drivers\PawnIO.sys", resolved);
    }

    [Fact]
    public void ResolveImagePath_strips_the_nt_native_path_prefix()
    {
        var resolved = PawnIoInstaller.ResolveImagePath(
            @"\??\C:\Windows\System32\DriverStore\FileRepository\pawnio.inf_amd64_abc\PawnIO.sys",
            @"C:\Windows");

        Assert.Equal(
            @"C:\Windows\System32\DriverStore\FileRepository\pawnio.inf_amd64_abc\PawnIO.sys",
            resolved);
    }

    [Fact]
    public void ResolveImagePath_returns_null_for_a_non_drive_nt_path()
    {
        var resolved = PawnIoInstaller.ResolveImagePath(
            @"\??\Volume{12345678-1234-1234-1234-123456789abc}\PawnIO.sys", @"C:\Windows");

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveImagePath_expands_the_systemroot_token()
    {
        var resolved = PawnIoInstaller.ResolveImagePath(
            @"\SystemRoot\System32\drivers\PawnIO.sys", @"C:\Windows");

        Assert.Equal(@"C:\Windows\System32\drivers\PawnIO.sys", resolved);
    }

    [Fact]
    public void ResolveImagePath_does_not_match_a_systemroot_prefix_without_a_separator()
    {
        // "\SystemRootFoo\..." must not be mistaken for the "\SystemRoot\..." token;
        // it falls through to the bare-relative-to-%SystemRoot% case unchanged.
        var resolved = PawnIoInstaller.ResolveImagePath(
            @"\SystemRootFoo\PawnIO.sys", @"C:\Windows");

        Assert.Equal(@"C:\Windows\SystemRootFoo\PawnIO.sys", resolved);
    }

    [Fact]
    public void ResolveImagePath_joins_a_bare_relative_path_to_systemroot()
    {
        var resolved = PawnIoInstaller.ResolveImagePath(
            @"System32\Drivers\PawnIO.sys", @"C:\Windows");

        Assert.Equal(@"C:\Windows\System32\Drivers\PawnIO.sys", resolved);
    }

    // --- ShouldPromptUpgrade ---

    private static readonly Version Bundled = new(2, 2, 0, 0);
    private static readonly Version Older = new(2, 1, 0, 0);
    private static readonly DateTime BootTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ShouldPromptUpgrade_returns_true_when_older_and_nothing_staged()
    {
        Assert.True(PawnIoInstaller.ShouldPromptUpgrade(Older, Bundled, marker: null, BootTime));
    }

    [Fact]
    public void ShouldPromptUpgrade_returns_false_when_this_version_is_already_staged_this_boot()
    {
        var marker = new PawnIoUpgradeMarker { StagedVersion = "2.2.0.0", StagedAtBootTimeUtc = BootTime };

        // Boot time recomputed a couple seconds later on the same boot.
        Assert.False(PawnIoInstaller.ShouldPromptUpgrade(Older, Bundled, marker, BootTime.AddSeconds(2)));
    }

    [Fact]
    public void ShouldPromptUpgrade_returns_true_when_rebooted_since_staging_but_still_old()
    {
        var marker = new PawnIoUpgradeMarker { StagedVersion = "2.2.0.0", StagedAtBootTimeUtc = BootTime };

        // A later boot instant means an actual reboot happened without the
        // staged package taking effect.
        Assert.True(PawnIoInstaller.ShouldPromptUpgrade(Older, Bundled, marker, BootTime.AddHours(1)));
    }

    [Fact]
    public void ShouldPromptUpgrade_returns_true_when_marker_staged_a_different_version()
    {
        var newerBundled = new Version(2, 3, 0, 0);
        var marker = new PawnIoUpgradeMarker { StagedVersion = "2.2.0.0", StagedAtBootTimeUtc = BootTime };

        Assert.True(PawnIoInstaller.ShouldPromptUpgrade(Older, newerBundled, marker, BootTime));
    }

    [Fact]
    public void ShouldPromptUpgrade_returns_false_when_installed_already_at_or_above_bundled()
    {
        var marker = new PawnIoUpgradeMarker { StagedVersion = "2.2.0.0", StagedAtBootTimeUtc = BootTime };

        Assert.False(PawnIoInstaller.ShouldPromptUpgrade(Bundled, Bundled, marker, BootTime));
    }
}
