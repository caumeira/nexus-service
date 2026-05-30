using System.IO;
using Nexus.Service.Lifecycle;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>Covers the XDG autostart .desktop write/remove via an injected config-home.</summary>
public class LinuxStartupProviderTests
{
    [Fact]
    public void SetEnabled_WritesThenRemovesAutostartDesktop()
    {
        using var t = new TempDir();
        var p = new LinuxStartupProvider(t.Root);
        var desktop = Path.Combine(t.Root, "autostart", "nexus.desktop");

        Assert.False(p.IsEnabled());

        Assert.True(p.SetEnabled(true, "/home/u/.local/share/nexus/Nexus", ""));
        Assert.True(p.IsEnabled());
        var content = File.ReadAllText(desktop);
        Assert.Contains("[Desktop Entry]", content);
        Assert.Contains("Exec=/home/u/.local/share/nexus/Nexus", content);

        Assert.True(p.SetEnabled(false, "", ""));
        Assert.False(p.IsEnabled());
        Assert.False(File.Exists(desktop));
    }

    [Fact]
    public void SetEnabled_QuotesPathWithSpacesAndAppendsArgs()
    {
        using var t = new TempDir();
        new LinuxStartupProvider(t.Root).SetEnabled(true, "/opt/My App/Nexus", "--flag");
        var content = File.ReadAllText(Path.Combine(t.Root, "autostart", "nexus.desktop"));
        Assert.Contains("Exec=\"/opt/My App/Nexus\" --flag", content);
    }
}
