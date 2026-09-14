using System.IO;
using Nexus.Service.Migration;

namespace Nexus.Service.Tests.Migration;

/// <summary>
/// The silent-uninstall command line the service hands Nexus 2's NSIS
/// uninstaller. Running it is Windows-only; what gets run is not.
/// </summary>
public sealed class Nexus2UninstallRulesTests
{
    private static readonly string Exe = Path.Combine("C:", "Users", "u", "AppData", "Local", "Programs", "HYTE Nexus", "Uninstall HYTE Nexus.exe");
    private static readonly string Root = Path.GetDirectoryName(Exe)!;

    [Fact]
    public void Quiet_string_wins_and_gets_the_in_place_marker()
    {
        var result = Nexus2UninstallRules.Compose($"\"{Exe}\" /currentuser /S", $"\"{Exe}\" /currentuser", null);

        Assert.NotNull(result);
        Assert.Equal(Exe, result!.Value.Exe);
        Assert.Equal($"/currentuser /S _?={Root}", result.Value.Arguments);
        Assert.Equal(Root, result.Value.Root);
    }

    [Fact]
    public void Plain_uninstall_string_gets_silent_flag_appended()
    {
        var result = Nexus2UninstallRules.Compose(null, $"\"{Exe}\" /allusers", null);

        Assert.NotNull(result);
        Assert.Equal($"/allusers /S _?={Root}", result!.Value.Arguments);
    }

    [Fact]
    public void Recorded_install_location_outranks_the_uninstaller_directory()
    {
        var recorded = Path.Combine("D:", "Apps", "Nexus2") + Path.DirectorySeparatorChar;

        var result = Nexus2UninstallRules.Compose($"\"{Exe}\" /S", null, recorded);

        Assert.Equal(recorded.TrimEnd(Path.DirectorySeparatorChar), result!.Value.Root);
        Assert.EndsWith($"_?={recorded.TrimEnd(Path.DirectorySeparatorChar)}", result.Value.Arguments);
    }

    [Fact]
    public void In_place_marker_is_never_quoted()
    {
        // NSIS reads everything after _?= verbatim and breaks on a quote, so a
        // root with spaces still goes bare.
        var result = Nexus2UninstallRules.Compose($"\"{Exe}\" /S", null, null);

        Assert.DoesNotContain("\"", result!.Value.Arguments);
        Assert.Contains(" ", Root);
    }

    [Fact]
    public void No_uninstall_string_means_nothing_to_run()
    {
        Assert.Null(Nexus2UninstallRules.Compose(null, null, Root));
        Assert.Null(Nexus2UninstallRules.Compose("  ", "", Root));
    }
}
