using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Widgets;
using Xunit;

namespace Nexus.Service.Tests.Widgets;

/// <summary>
/// The <c>driver</c> manifest block is a first-party-only grant: the registry must
/// keep it for an allowlisted appId and silently drop it from everyone else (while
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

    private AppRegistry NewRegistry() => new(() => new List<AppInstallPaths.Root>
    {
        new(_root, AppInstallPaths.Source.Bundled),
    });

    [Fact]
    public void Keeps_driver_block_for_allowlisted_app()
    {
        WriteApp("com.ibuypower.control", withDriver: true);

        var registry = NewRegistry();
        Assert.True(registry.TryGet("com.ibuypower.control", out var entry));
        Assert.NotNull(entry.Manifest.Driver);
        Assert.Equal("ibp-aw5", entry.Manifest.Driver!.ToolId);
        Assert.Equal("Apaltek", entry.Manifest.Driver.Variants["0405"]);
        Assert.Equal("system", entry.Manifest.Driver.Launch!.Session);
        Assert.Equal("3402", entry.Manifest.Driver.Match!.Vid);
    }

    [Fact]
    public void Drops_driver_block_from_non_allowlisted_app_but_keeps_the_app()
    {
        WriteApp("com.hellonexus.fixture-basic", withDriver: true);

        var registry = NewRegistry();
        Assert.True(registry.TryGet("com.hellonexus.fixture-basic", out var entry));
        Assert.Null(entry.Manifest.Driver); // dropped — the app still loads
    }

    [Fact]
    public void No_driver_block_is_unaffected()
    {
        WriteApp("com.hellonexus.fixture-basic", withDriver: false);

        var registry = NewRegistry();
        Assert.True(registry.TryGet("com.hellonexus.fixture-basic", out var entry));
        Assert.Null(entry.Manifest.Driver);
    }

    private void WriteApp(string id, bool withDriver)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "widget.mjs"), "export default function mount(){}\n");

        var driver = withDriver
            ? ",\"driver\":{\"toolId\":\"ibp-aw5\",\"match\":{\"vid\":\"3402\",\"pids\":[\"0405\"]}," +
              "\"variants\":{\"0405\":\"Apaltek\"},\"manifestUrlBase\":\"https://assets.hellonexus.com/ibp_aw5_aio\"," +
              "\"filePattern\":\"iBUYPOWER_AW5*.exe\",\"launch\":{\"session\":\"system\",\"hidden\":true}}"
            : "";

        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            "{\"schema\":\"nexus.app/1\",\"id\":\"" + id + "\",\"name\":\"x\",\"version\":\"1.0.0\"," +
            "\"min_nexus_version\":\"0.42.0\",\"runtime\":\"sdk\",\"surfaces\":[\"dashboard\"]," +
            "\"sizes\":[\"2x2\"],\"capabilities\":{}" + driver + "}");
    }
}
