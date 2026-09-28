using System.Text.Json;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Widgets;

public class AppManifestGridSizesTests
{
    private const string Head = """{"schema":"nexus.app/1","id":"com.example.fish","name":"Fish","version":"1.0.0","sizes":["2x2","4x2","4x4"]""";

    [Fact]
    public void ReadsGridSizesAndHandsThemToTheDashboardAsGridSizes()
    {
        var manifest = JsonSerializer.Deserialize(Head + ""","grid_sizes":["4x4"]}""", AppJsonContext.Default.AppManifest)!;
        Assert.Equal(["4x4"], manifest.GridSizes);

        var json = JsonSerializer.Serialize(new AppInstalledListing { GridSizes = manifest.GridSizes }, AppJsonContext.Default.AppInstalledListing);
        Assert.Contains("\"gridSizes\":[\"4x4\"]", json);
    }

    [Fact]
    public void LeavesGridSizesOutWhenTheManifestHasNone()
    {
        var manifest = JsonSerializer.Deserialize(Head + "}", AppJsonContext.Default.AppManifest)!;
        Assert.Null(manifest.GridSizes);

        var json = JsonSerializer.Serialize(new AppInstalledListing { GridSizes = manifest.GridSizes }, AppJsonContext.Default.AppInstalledListing);
        Assert.DoesNotContain("gridSizes", json);
    }
}
