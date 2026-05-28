using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Widgets;

public class WidgetRegistryTests : IDisposable
{
    private readonly string _root;

    public WidgetRegistryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "nexus-widgets-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        CopyFixture(Path.Combine(AppContext.BaseDirectory, "Widgets", "Fixtures", "widgets-basic"), _root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private WidgetRegistry NewRegistry(WidgetInstallPaths.Source source = WidgetInstallPaths.Source.User)
    {
        return new WidgetRegistry(() => new List<WidgetInstallPaths.Root>
        {
            new(_root, source),
        });
    }

    [Fact]
    public void Discovers_fixture_widget()
    {
        var registry = NewRegistry();
        Assert.True(registry.TryGet("com.nexusqos.fixture-basic", out var entry));
        Assert.Equal("1.0.0", entry.Manifest.Version);
        Assert.Equal("nexus.widget/2", entry.Manifest.Schema);
        Assert.Single(entry.Manifest.Surfaces, "dashboard");
        Assert.Equal(new List<string> { "cpu.*" }, entry.Manifest.Capabilities.SensorsRead);
        Assert.Equal("2x2", entry.Manifest.DefaultSize);
        Assert.True(entry.Manifest.View.ValueKind == System.Text.Json.JsonValueKind.Object);
    }

    [Fact]
    public void Skips_bundle_when_folder_name_does_not_match_id()
    {
        // Rename the folder so manifest.id no longer matches the directory.
        var src = Path.Combine(_root, "com.nexusqos.fixture-basic");
        var dst = Path.Combine(_root, "imposter.bundle");
        Directory.Move(src, dst);

        var registry = NewRegistry();
        Assert.False(registry.TryGet("com.nexusqos.fixture-basic", out _));
        Assert.False(registry.TryGet("imposter.bundle", out _));
    }

    [Fact]
    public void Skips_bundle_when_view_block_is_missing()
    {
        var manifest = Path.Combine(_root, "com.nexusqos.fixture-basic", "manifest.json");
        File.WriteAllText(manifest, """
        {
          "schema": "nexus.widget/2",
          "id": "com.nexusqos.fixture-basic",
          "name": "x",
          "version": "1.0.0",
          "min_nexus_version": "0.42.0",
          "surfaces": ["dashboard"],
          "capabilities": {}
        }
        """);

        var registry = NewRegistry();
        Assert.False(registry.TryGet("com.nexusqos.fixture-basic", out _));
    }

    [Fact]
    public void Skips_worker_bundle_when_worker_js_is_missing()
    {
        var manifest = Path.Combine(_root, "com.nexusqos.fixture-basic", "manifest.json");
        File.WriteAllText(manifest, """
        {
          "schema": "nexus.widget/2",
          "id": "com.nexusqos.fixture-basic",
          "name": "x",
          "version": "1.0.0",
          "min_nexus_version": "0.42.0",
          "surfaces": ["dashboard"],
          "capabilities": { "code": "worker" },
          "view": { "type": "text", "text": "hi" }
        }
        """);

        var registry = NewRegistry();
        Assert.False(registry.TryGet("com.nexusqos.fixture-basic", out _));
    }

    [Fact]
    public void Skips_bundle_when_manifest_is_malformed()
    {
        var manifest = Path.Combine(_root, "com.nexusqos.fixture-basic", "manifest.json");
        File.WriteAllText(manifest, "{ not valid json");

        var registry = NewRegistry();
        Assert.False(registry.TryGet("com.nexusqos.fixture-basic", out _));
    }

    [Fact]
    public void Skips_bundle_with_unknown_schema()
    {
        var manifest = Path.Combine(_root, "com.nexusqos.fixture-basic", "manifest.json");
        File.WriteAllText(manifest, """
        {
          "schema": "nexus.widget/9999",
          "id": "com.nexusqos.fixture-basic",
          "name": "x",
          "version": "1.0.0",
          "min_nexus_version": "0.42.0",
          "surfaces": ["dashboard"],
          "capabilities": {}
        }
        """);

        var registry = NewRegistry();
        Assert.False(registry.TryGet("com.nexusqos.fixture-basic", out _));
    }

    [Fact]
    public void Records_install_source()
    {
        var registry = NewRegistry(WidgetInstallPaths.Source.Bundled);
        Assert.True(registry.TryGet("com.nexusqos.fixture-basic", out var entry));
        Assert.Equal(WidgetInstallPaths.Source.Bundled, entry.Source);
    }

    [Fact]
    public void First_root_wins_when_id_appears_in_multiple_roots()
    {
        // Build a second root that also contains the same id.
        var secondRoot = Path.Combine(Path.GetTempPath(), "nexus-widgets-tests-2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(secondRoot);
        CopyFixture(Path.Combine(AppContext.BaseDirectory, "Widgets", "Fixtures", "widgets-basic"), secondRoot);
        try
        {
            var registry = new WidgetRegistry(() => new List<WidgetInstallPaths.Root>
            {
                new(_root, WidgetInstallPaths.Source.Dev),
                new(secondRoot, WidgetInstallPaths.Source.User),
            });

            Assert.True(registry.TryGet("com.nexusqos.fixture-basic", out var entry));
            Assert.Equal(WidgetInstallPaths.Source.Dev, entry.Source);
        }
        finally
        {
            try { Directory.Delete(secondRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void CopyFixture(string source, string destination)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, destination, StringComparison.Ordinal));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, destination, StringComparison.Ordinal), overwrite: true);
        }
    }
}
