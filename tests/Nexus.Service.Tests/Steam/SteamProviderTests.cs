using System;
using System.IO;
using System.Linq;
using Nexus.Service.Steam;
using Xunit;

namespace Nexus.Service.Tests.Steam;

public class SteamProviderTests
{
    // homeOverride bypasses SpecialFolder.UserProfile entirely, so this
    // needs no process-wide HOME mutation - a concurrently running test that
    // resolves UserProfile is unaffected.
    [Fact]
    public void GetLoginUsersPaths_IncludesTheNativeSteamPathAndThisPlatformsPaths()
    {
        var fakeHome = Path.Combine(Path.GetTempPath(), "nexus-steam-home");

        var paths = SteamProvider.GetLoginUsersPaths(fakeHome).ToList();

        Assert.Contains(Path.Combine(fakeHome, ".steam", "steam", "config", "loginusers.vdf"), paths);

        if (OperatingSystem.IsLinux())
        {
            Assert.Contains(Path.Combine(fakeHome, ".local", "share", "Steam", "config", "loginusers.vdf"), paths);
            Assert.Contains(
                Path.Combine(fakeHome, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", "config", "loginusers.vdf"),
                paths);
        }
        if (OperatingSystem.IsMacOS())
        {
            Assert.Contains(Path.Combine(fakeHome, "Library", "Application Support", "Steam", "config", "loginusers.vdf"), paths);
        }
    }
}
