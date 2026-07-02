using System;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxRoutesTests
{
    [Fact]
    public void ResolveAvailablePresets_returns_only_the_ids_the_panel_reported()
    {
        var presets = TryxRoutes.ResolveAvailablePresets(new[] { "default_01", "default_02" });

        Assert.Equal(2, presets.Count);
        Assert.Equal("default_01", presets[0].Id);
        Assert.Equal("default_02", presets[1].Id);
        Assert.All(presets, p => Assert.False(string.IsNullOrEmpty(p.Name)));
    }

    [Fact]
    public void ResolveAvailablePresets_falls_back_to_the_first_six_when_the_panel_has_not_reported_yet()
    {
        var presets = TryxRoutes.ResolveAvailablePresets(Array.Empty<string>());

        Assert.Equal(6, presets.Count);
        Assert.Equal(
            new[] { "default_01", "default_02", "default_03", "default_04", "default_05", "default_06" },
            presets.ConvertAll(p => p.Id));
    }

    [Fact]
    public void ResolveAvailablePresets_ignores_ids_that_are_not_in_the_known_catalog()
    {
        var presets = TryxRoutes.ResolveAvailablePresets(new[] { "default_01", "start", "screensaver" });

        Assert.Single(presets);
        Assert.Equal("default_01", presets[0].Id);
    }
}
