using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

/// <summary>
/// JsonConfigStore's settings path composes NexusDataPaths' resolved root
/// with settings.json, so it inherits the NEXUS_DATA_ROOT override.
/// ResolveSettingsPath(string?) is a pure test seam - it never reads the
/// environment, so these cases carry no cross-test race risk regardless of
/// suite parallelization.
/// </summary>
public class JsonConfigStoreDataRootTests
{
    [Fact]
    public void ResolveSettingsPath_appends_settings_json_to_a_non_blank_override()
    {
        var overrideRoot = Path.Combine(Path.GetTempPath(), "nexus-data-root-test-" + Guid.NewGuid().ToString("N"));

        var path = JsonConfigStore.ResolveSettingsPath(overrideRoot);

        Assert.Equal(Path.Combine(overrideRoot, "settings.json"), path);
    }

    [Fact]
    public void ResolveSettingsPath_expands_a_relative_override_to_an_absolute_path()
    {
        var relative = "nexus-data-root-test-" + Guid.NewGuid().ToString("N");

        var path = JsonConfigStore.ResolveSettingsPath(relative);

        Assert.True(Path.IsPathRooted(path));
        Assert.Equal(Path.Combine(Path.GetFullPath(relative), "settings.json"), path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveSettingsPath_falls_back_to_the_platform_default_for_a_blank_override(string? overrideRoot)
    {
        Assert.Equal(JsonConfigStore.ResolveSettingsPath(null), JsonConfigStore.ResolveSettingsPath(overrideRoot));
    }

    // JsonConfigStore.ResolveDataDirectory() and the parameterless ctor read
    // the real NEXUS_DATA_ROOT environment variable (via NexusDataPaths), so
    // this one case saves and restores it around a single synchronous call,
    // mirroring the NexusRoot_reads_the_override_from_the_environment case in
    // NexusDataPathsTests.
    [Fact]
    public void ResolveDataDirectory_reads_the_override_from_the_environment()
    {
        var original = Environment.GetEnvironmentVariable("NEXUS_DATA_ROOT");
        var overrideRoot = Path.Combine(Path.GetTempPath(), "nexus-data-root-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("NEXUS_DATA_ROOT", overrideRoot);
        try
        {
            Assert.Equal(overrideRoot, JsonConfigStore.ResolveDataDirectory());
            Assert.Equal(Path.Combine(overrideRoot, "settings.json"), new JsonConfigStore().SettingsPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NEXUS_DATA_ROOT", original);
        }
    }
}
