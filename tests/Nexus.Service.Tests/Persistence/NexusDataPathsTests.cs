using System;
using System.IO;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Persistence;

public class NexusDataPathsTests
{
    [Fact]
    public void NexusRoot_ends_with_the_Nexus_folder_name()
    {
        var root = NexusDataPaths.NexusRoot();

        Assert.Equal("Nexus", Path.GetFileName(root));
    }

    [Fact]
    public void DatabaseDir_is_a_db_subfolder_of_NexusRoot()
    {
        var expected = Path.Combine(NexusDataPaths.NexusRoot(), "db");

        Assert.Equal(expected, NexusDataPaths.DatabaseDir());
    }

    [Fact]
    public void NexusRoot_is_stable_across_calls()
    {
        Assert.Equal(NexusDataPaths.NexusRoot(), NexusDataPaths.NexusRoot());
    }

    [Fact]
    public void NexusRoot_matches_the_platform_specific_config_root()
    {
        var root = NexusDataPaths.NexusRoot();

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Equal(Path.Combine(home, "Library", "Application Support", "Nexus"), root);
        }
        else if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            Assert.Equal(Path.Combine(programData, "Nexus"), root);
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(xdg))
            {
                xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }
            Assert.Equal(Path.Combine(xdg, "Nexus"), root);
        }
    }

    // ResolveRoot(string?) is the test seam for the override's precedence and
    // expansion logic: it never touches the real environment, so these cases
    // carry no cross-test race risk regardless of suite parallelization.

    [Fact]
    public void ResolveRoot_honors_a_non_blank_override()
    {
        var overrideRoot = Path.Combine(Path.GetTempPath(), "nexus-data-root-test-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(overrideRoot, NexusDataPaths.ResolveRoot(overrideRoot));
    }

    [Fact]
    public void ResolveRoot_expands_a_relative_override_to_an_absolute_path()
    {
        var relative = "nexus-data-root-test-" + Guid.NewGuid().ToString("N");

        var root = NexusDataPaths.ResolveRoot(relative);

        Assert.True(Path.IsPathRooted(root));
        Assert.Equal(Path.GetFullPath(relative), root);
    }

    [Fact]
    public void ResolveRoot_trims_surrounding_whitespace_from_the_override()
    {
        var overrideRoot = Path.Combine(Path.GetTempPath(), "nexus-data-root-test-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(overrideRoot, NexusDataPaths.ResolveRoot("  " + overrideRoot + "  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveRoot_falls_back_to_the_platform_default_for_a_blank_override(string? overrideRoot)
    {
        Assert.Equal(NexusDataPaths.ResolveRoot(null), NexusDataPaths.ResolveRoot(overrideRoot));
    }

    // NexusRoot() itself reads the real NEXUS_DATA_ROOT environment variable,
    // so this one case saves and restores it around a single synchronous call,
    // mirroring the NEXUS_TEST_HOST idiom in McpServerHostLifecycleTests.
    [Fact]
    public void NexusRoot_reads_the_override_from_the_environment()
    {
        var original = Environment.GetEnvironmentVariable("NEXUS_DATA_ROOT");
        var overrideRoot = Path.Combine(Path.GetTempPath(), "nexus-data-root-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("NEXUS_DATA_ROOT", overrideRoot);
        try
        {
            Assert.Equal(overrideRoot, NexusDataPaths.NexusRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NEXUS_DATA_ROOT", original);
        }
    }
}
