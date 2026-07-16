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
}
