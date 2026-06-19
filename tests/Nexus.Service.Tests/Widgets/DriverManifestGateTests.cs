using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Widgets;
using Xunit;

namespace Nexus.Service.Tests.Widgets;

/// <summary>
/// The <c>driver</c> manifest block is honored only for a bundled app: the registry
/// keeps it for a Bundled-source app and drops it from a user/dev install (while
/// still loading the app's widget facet).
/// </summary>
public class DriverManifestGateTests : IDisposable
{
    private readonly string _root;

    public DriverManifestGateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "nexus-drivergate-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private AppRegistry NewRegistry(AppInstallPaths.Source source) => new(() => new List<AppInstallPaths.Root>
    {
        new(_root, source),
    });

    [Fact]
    public void Keeps_driver_block_for_a_bundled_app()
    {
        WriteApp("com.example.cooler", withDriver: true);

        var registry = NewRegistry(AppInstallPaths.Source.Bundled);
        Assert.True(registry.TryGet("com.example.cooler", out var entry));
        Assert.NotNull(entry.Manifest.Driver);
        Assert.Equal("acme-cooler", entry.Manifest.Driver!.ToolId);
        Assert.Equal("VariantA", entry.Manifest.Driver.Variants["0001"]);
        Assert.Equal("system", entry.Manifest.Driver.Launch!.Session);
        Assert.Equal("1234", entry.Manifest.Driver.Match!.Vid);
    }

    [Fact]
    public void Drops_driver_block_from_a_user_installed_app_but_keeps_the_app()
    {
        WriteApp("com.example.cooler", withDriver: true);

        var registry = NewRegistry(AppInstallPaths.Source.User);
        Assert.True(registry.TryGet("com.example.cooler", out var entry));
        Assert.Null(entry.Manifest.Driver); // dropped - the app still loads
    }

    [Fact]
    public void No_driver_block_is_unaffected()
    {
        WriteApp("com.example.cooler", withDriver: false);

        var registry = NewRegistry(AppInstallPaths.Source.Bundled);
        Assert.True(registry.TryGet("com.example.cooler", out var entry));
        Assert.Null(entry.Manifest.Driver);
    }

    private void WriteApp(string id, bool withDriver)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "widget.mjs"), "export default function mount(){}\n");

        var driver = withDriver
            ? ",\"driver\":{\"toolId\":\"acme-cooler\",\"match\":{\"vid\":\"1234\",\"pids\":[\"0001\"]}," +
              "\"variants\":{\"0001\":\"VariantA\"},\"manifestUrlBase\":\"https://assets.hellonexus.com/acme_cooler\"," +
              "\"filePattern\":\"MyDriver*.exe\",\"launch\":{\"session\":\"system\",\"hidden\":true}}"
            : "";

        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            "{\"schema\":\"nexus.app/1\",\"id\":\"" + id + "\",\"name\":\"x\",\"version\":\"1.0.0\"," +
            "\"min_nexus_version\":\"0.42.0\",\"runtime\":\"sdk\",\"surfaces\":[\"dashboard\"]," +
            "\"sizes\":[\"2x2\"],\"capabilities\":{}" + driver + "}");
    }
}
