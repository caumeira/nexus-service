using System;
using System.IO;
using Nexus.Service.Migration;

namespace Nexus.Service.Tests.Migration;

/// <summary>
/// The presence-vs-leftover rule behind the returning-user screen. The probes
/// that feed it are Windows-only (registry, scheduled task, user profiles),
/// but what their results mean is not, so it runs on every OS.
/// </summary>
public sealed class Nexus2DetectionRulesTests
{
    private static Nexus2DetectionResult Compose(
        bool installed = false, bool running = false,
        bool importAvailable = false, bool autostartTaskPresent = false) =>
        Nexus2DetectionRules.Compose(installed, running, importAvailable, autostartTaskPresent, null, null);

    [Fact]
    public void Leftover_config_data_alone_is_not_a_detection()
    {
        var result = Compose(importAvailable: true);

        Assert.False(result.Detected);
        Assert.True(result.ImportAvailable);
    }

    [Fact]
    public void Leftover_autostart_task_alone_is_not_a_detection()
    {
        var result = Compose(autostartTaskPresent: true);

        Assert.False(result.Detected);
        Assert.True(result.AutostartTaskPresent);
    }

    [Fact]
    public void Live_uninstall_entry_is_a_detection()
    {
        Assert.True(Compose(installed: true).Detected);
    }

    [Fact]
    public void Running_process_is_a_detection_without_an_uninstall_entry()
    {
        var result = Compose(running: true);

        Assert.True(result.Detected);
        Assert.True(result.Running);
    }

    [Fact]
    public void Nothing_found_is_not_a_detection()
    {
        Assert.False(Compose().Detected);
    }

    [Fact]
    public void Compose_carries_version_and_install_location_through()
    {
        var result = Nexus2DetectionRules.Compose(
            installed: true, running: false, importAvailable: true, autostartTaskPresent: true,
            version: "2.16.0", installLocation: @"C:\Program Files\HYTE Nexus");

        Assert.Equal("2.16.0", result.Version);
        Assert.Equal(@"C:\Program Files\HYTE Nexus", result.InstallLocation);
    }

    [Fact]
    public void Uninstall_entry_is_live_when_its_install_directory_exists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus2-detect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(Nexus2DetectionRules.UninstallEntryIsLive(dir));
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void Uninstall_entry_is_stale_when_its_install_directory_is_gone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus2-detect-" + Guid.NewGuid().ToString("N"));

        Assert.False(Nexus2DetectionRules.UninstallEntryIsLive(dir));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Uninstall_entry_without_a_recorded_location_counts(string? installLocation)
    {
        Assert.True(Nexus2DetectionRules.UninstallEntryIsLive(installLocation));
    }
}
