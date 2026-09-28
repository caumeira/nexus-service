using System.Text.Json;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Widgets;

public class AppManifestImmersiveDoubleSwipeTests
{
    private const string Head = """{"schema":"nexus.app/1","id":"com.example.fish","name":"Fish","version":"1.0.0","sizes":["2x2"],"immersive":true""";

    [Fact]
    public void ReadsTheDoubleSwipeFlagAndHandsItToTheDashboard()
    {
        var manifest = JsonSerializer.Deserialize(Head + ""","immersive_double_swipe":true}""", AppJsonContext.Default.AppManifest)!;
        Assert.True(manifest.ImmersiveDoubleSwipe);

        var json = JsonSerializer.Serialize(new AppInstalledListing { ImmersiveDoubleSwipe = manifest.ImmersiveDoubleSwipe }, AppJsonContext.Default.AppInstalledListing);
        Assert.Contains("\"immersiveDoubleSwipe\":true", json);
    }

    [Fact]
    public void LeavesDoubleSwipeOffWhenTheManifestDoesNotSetIt()
    {
        var manifest = JsonSerializer.Deserialize(Head + "}", AppJsonContext.Default.AppManifest)!;
        Assert.False(manifest.ImmersiveDoubleSwipe);
    }
}
