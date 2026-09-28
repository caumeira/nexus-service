using System.Text.Json;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Widgets;

public class AppManifestAudioTests
{
    private const string Head = """{"schema":"nexus.app/1","id":"com.example.fish","name":"Fish","version":"1.0.0","sizes":["2x2"]""";

    [Fact]
    public void ReadsTheAudioCapabilityAndHandsItToTheDashboard()
    {
        var manifest = JsonSerializer.Deserialize(Head + ""","capabilities":{"audio":true}}""", AppJsonContext.Default.AppManifest)!;
        Assert.True(manifest.Capabilities.Audio);

        var json = JsonSerializer.Serialize(new AppInstalledListing { Capabilities = manifest.Capabilities }, AppJsonContext.Default.AppInstalledListing);
        Assert.Contains("\"audio\":true", json);
    }

    [Fact]
    public void LeavesAudioOffWhenTheManifestDoesNotAskForIt()
    {
        var manifest = JsonSerializer.Deserialize(Head + "}", AppJsonContext.Default.AppManifest)!;
        Assert.False(manifest.Capabilities.Audio);
    }
}
