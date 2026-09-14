using System.IO;
using Nexus.Service.Migration;

namespace Nexus.Service.Tests.Migration;

/// <summary>
/// The silent-uninstall command line the service hands Nexus 2's NSIS
/// uninstaller, and the trust rules on the ARP values it is built from.
/// Running it is Windows-only; what gets run is not.
/// </summary>
public sealed class Nexus2UninstallRulesTests
{
    private static readonly string Root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "Programs", "HYTE Nexus");
    private static readonly string Exe = Path.Combine(Root, "Uninstall HYTE Nexus.exe");

    [Fact]
    public void Quiet_string_wins_and_gets_the_in_place_marker()
    {
        var result = Nexus2UninstallRules.Compose($"\"{Exe}\" /currentuser /S", $"\"{Exe}\" /currentuser");

        Assert.NotNull(result);
        Assert.Equal(Exe, result!.Value.Exe);
        Assert.Equal($"/currentuser /S _?={Root}", result.Value.Arguments);
        Assert.Equal(Root, result.Value.Root);
    }

    [Fact]
    public void Plain_uninstall_string_gets_silent_flag_appended()
    {
        var result = Nexus2UninstallRules.Compose(null, $"\"{Exe}\" /allusers");

        Assert.NotNull(result);
        Assert.Equal($"/allusers /S _?={Root}", result!.Value.Arguments);
    }

    [Fact]
    public void In_place_marker_is_never_quoted()
    {
        // NSIS reads everything after _?= verbatim and breaks on a quote, so a
        // root with spaces still goes bare.
        var result = Nexus2UninstallRules.Compose($"\"{Exe}\" /S", null);

        Assert.DoesNotContain("\"", result!.Value.Arguments);
        Assert.Contains(" ", Root);
    }

    [Fact]
    public void No_uninstall_string_means_nothing_to_run()
    {
        Assert.Null(Nexus2UninstallRules.Compose(null, null));
        Assert.Null(Nexus2UninstallRules.Compose("  ", ""));
    }

    [Theory]
    [InlineData("Programs", "Uninstall HYTE Nexus.exe")]
    [InlineData("HYTE Nexus", "Uninstall.exe")]
    [InlineData("HYTE Nexus", "cmd.exe")]
    public void Refuses_any_exe_that_is_not_the_uninstaller_in_its_own_directory(string dir, string file)
    {
        var exe = Path.Combine(Path.GetFullPath(Path.GetTempPath()), dir, file);

        Assert.False(Nexus2UninstallRules.IsUninstallerPath(exe));
        Assert.Null(Nexus2UninstallRules.Compose($"\"{exe}\" /S", null));
    }

    [Fact]
    public void Accepts_the_uninstaller_regardless_of_case()
    {
        Assert.True(Nexus2UninstallRules.IsUninstallerPath(Exe.ToUpperInvariant()));
    }

    [Fact]
    public void Trusts_only_the_signers_nexus2_shipped_under()
    {
        Assert.True(Nexus2UninstallRules.IsTrustedSigner(
            "OID.2.5.4.15=Private Organization, CN=American Future Technology Corp., O=iBUYPOWER (American Future Technology Corp.), C=US"));
        Assert.True(Nexus2UninstallRules.IsTrustedSigner("CN=HYTE, O=HYTE, C=US"));
        Assert.False(Nexus2UninstallRules.IsTrustedSigner("CN=Contoso Ltd, O=Contoso, C=US"));
        Assert.False(Nexus2UninstallRules.IsTrustedSigner(""));
    }
}
