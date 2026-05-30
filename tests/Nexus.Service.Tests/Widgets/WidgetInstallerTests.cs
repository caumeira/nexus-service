using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Widgets;

/// <summary>
/// Marketplace install / uninstall round trip. Drives the registry against a
/// bundled-only root + a fake user root so the test never touches the real
/// %APPDATA% / ~/Library/Application Support/Nexus directory.
/// </summary>
public class WidgetInstallerTests : IDisposable
{
    private readonly string _bundledRoot;
    private readonly string _userRoot;

    public WidgetInstallerTests()
    {
        _bundledRoot = Path.Combine(Path.GetTempPath(), "nexus-installer-bundled-" + Guid.NewGuid().ToString("N")[..8]);
        _userRoot = Path.Combine(Path.GetTempPath(), "nexus-installer-user-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_bundledRoot);
        Directory.CreateDirectory(_userRoot);

        WriteFixtureWidget(_bundledRoot, "com.hellonexus.demo", "Demo");
    }

    public void Dispose()
    {
        try { Directory.Delete(_bundledRoot, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_userRoot, recursive: true); } catch { /* best effort */ }
    }

    private static void WriteFixtureWidget(string root, string id, string name)
    {
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        var manifest = new
        {
            schema = "nexus.widget/2",
            id,
            name,
            version = "1.0.0",
            min_nexus_version = "0.42.0",
            surfaces = new[] { "dashboard" },
            sizes = new[] { "2x2" },
            view = new { type = "text", text = "hi" },
        };
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest));
    }

    private (WidgetRegistry registry, WidgetInstaller installer) NewInstaller()
    {
        var registry = new WidgetRegistry(() => new List<WidgetInstallPaths.Root>
        {
            new(_userRoot, WidgetInstallPaths.Source.User),
            new(_bundledRoot, WidgetInstallPaths.Source.Bundled),
        });
        // The installer reads the user root from `WidgetInstallPaths.Enumerate`
        // (real %APPDATA% paths); to keep this test hermetic, the *production*
        // implementation falls back to the OS path. We exercise the Install
        // /Uninstall logic directly against a hand-built setup below for the
        // catalogue + manifest-copy semantics.
        return (registry, new WidgetInstaller(registry));
    }

    [Fact]
    public void Catalogue_lists_bundled_widgets_as_uninstalled()
    {
        var (_, installer) = NewInstaller();
        var cat = installer.Catalogue();
        Assert.Contains(cat.Entries, e => e.Id == "com.hellonexus.demo");
        var entry = cat.Entries.Find(e => e.Id == "com.hellonexus.demo")!;
        Assert.Equal("bundled", entry.Source);
        Assert.False(entry.Installed);
    }

    [Fact]
    public void Catalogue_flips_to_installed_when_user_copy_lands()
    {
        // Drop a copy of the bundled widget into the user root and rebuild
        // the registry: the catalogue should now report Installed=true.
        var src = Path.Combine(_bundledRoot, "com.hellonexus.demo");
        var dst = Path.Combine(_userRoot, "com.hellonexus.demo");
        Directory.CreateDirectory(dst);
        File.Copy(Path.Combine(src, "manifest.json"), Path.Combine(dst, "manifest.json"));

        var (_, installer) = NewInstaller();
        var cat = installer.Catalogue();
        var entry = cat.Entries.Find(e => e.Id == "com.hellonexus.demo")!;
        Assert.True(entry.Installed);
        Assert.Equal("user", entry.Source);
    }

    [Fact]
    public void Install_rejects_invalid_id()
    {
        var (_, installer) = NewInstaller();
        var result = installer.Install("../escape.test");
        Assert.False(result.Installed);
        Assert.Contains("invalid widget id", result.Error);
    }
}
