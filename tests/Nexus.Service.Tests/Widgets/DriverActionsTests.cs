using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Common.ExternalTools;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Widgets;
using Nexus.Service.Widgets.AppActions;
using Xunit;

namespace Nexus.Service.Tests.Widgets;

/// <summary>
/// End-to-end-ish wiring of the driver dispatch actions: authorization against the
/// first-party allowlist, status reporting, and resolve→launch→terminate through the
/// external-tool manager. Exercises the action handlers directly with a built service
/// provider (the dispatch route's allowlist + __callerAppId injection is its own concern).
/// </summary>
public class DriverActionsTests : IDisposable
{
    private readonly string _root;     // apps root
    private readonly string _cache;    // tool cache root

    public DriverActionsTests()
    {
        var b = Path.Combine(Path.GetTempPath(), "nexus-driveractions-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _root = Path.Combine(b, "apps");
        _cache = Path.Combine(b, "cache");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_cache);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { }
    }

    [Fact]
    public async Task Refuses_a_non_allowlisted_caller()
    {
        var sp = BuildProvider(usb: new StubUsb());
        var status = await Invoke(sp, "driver.launch", callerAppId: "com.evil.app");
        Assert.Equal("Failed", status);
    }

    [Fact]
    public async Task Reports_NoDevice_when_no_matching_hardware()
    {
        WriteIbpApp(withDriverBinary: false);
        var sp = BuildProvider(usb: new StubUsb()); // empty bus
        var status = await Invoke(sp, "driver.status", callerAppId: "com.ibuypower.control");
        Assert.Equal("NoDevice", status);
    }

    [Fact]
    public async Task Launches_then_terminates_when_device_present()
    {
        if (OperatingSystem.IsWindows()) return; // real child process; Unix only (see manager tests)

        WriteIbpApp(withDriverBinary: true);
        var usb = new StubUsb(new UsbDeviceEntry { VendorId = 0x3402, ProductId = 0x0405 });
        var sp = BuildProvider(usb);

        var launched = await Invoke(sp, "driver.launch", "com.ibuypower.control");
        Assert.Equal("Running", launched);

        var status = await Invoke(sp, "driver.status", "com.ibuypower.control");
        Assert.Equal("Running", status);

        var terminated = await Invoke(sp, "driver.terminate", "com.ibuypower.control");
        Assert.Equal("NotRunning", terminated);
    }

    // ── Harness ──

    private ServiceProvider BuildProvider(IUsbEnumerator usb)
    {
        var registry = new AppRegistry(() => new List<AppInstallPaths.Root>
        {
            new(_root, AppInstallPaths.Source.Bundled),
        });
        var manager = new ExternalToolManager(new HttpClient(new ExplodingHandler()), _cache);

        var services = new ServiceCollection();
        services.AddSingleton(registry);
        services.AddSingleton(manager);
        services.AddSingleton(usb);
        return services.BuildServiceProvider();
    }

    private static async Task<string?> Invoke(IServiceProvider sp, string action, string callerAppId)
    {
        var registry = new AppActionRegistry();
        DriverActions.RegisterAll(registry);
        Assert.True(registry.TryGet(action, out var handler));

        var args = new Dictionary<string, JsonElement>
        {
            ["__callerAppId"] = JsonDocument.Parse("\"" + callerAppId + "\"").RootElement.Clone(),
        };
        var result = await handler(sp, args, CancellationToken.None);
        return result?.GetProperty("status").GetString();
    }

    private void WriteIbpApp(bool withDriverBinary)
    {
        var dir = Path.Combine(_root, "com.ibuypower.control");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "widget.mjs"), "export default function mount(){}\n");
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            "{\"schema\":\"nexus.app/1\",\"id\":\"com.ibuypower.control\",\"name\":\"iBUYPOWER\",\"version\":\"1.0.0\"," +
            "\"min_nexus_version\":\"0.42.0\",\"runtime\":\"sdk\",\"surfaces\":[\"dashboard\"],\"sizes\":[\"2x2\"]," +
            "\"capabilities\":{\"dispatch\":[\"driver.status\",\"driver.launch\",\"driver.terminate\"]}," +
            "\"driver\":{\"toolId\":\"ibp-aw5\",\"match\":{\"vid\":\"3402\",\"pids\":[\"0405\"]}," +
            "\"variants\":{\"0405\":\"Apaltek\"},\"manifestUrlBase\":\"https://assets.hellonexus.com/ibp_aw5_aio\"," +
            "\"filePattern\":\"iBUYPOWER_AW5*.exe\",\"launch\":{\"session\":\"system\",\"hidden\":true}}}");

        if (!withDriverBinary) return;

        // Preload a verified sleeper under drivers/<variant>/ (the OEM offline path).
        var variantDir = Path.Combine(dir, "drivers", "Apaltek");
        Directory.CreateDirectory(variantDir);
        var sleeper = Path.Combine(variantDir, "sleeper.sh");
        File.WriteAllText(sleeper, "#!/bin/sh\nsleep 30\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(sleeper,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        var payload = File.ReadAllBytes(sleeper);
        File.WriteAllText(Path.Combine(variantDir, "bundled.json"),
            "{\"fileName\":\"sleeper.sh\",\"sha256\":\"" + Sha256Hex(payload) + "\",\"size\":" + payload.Length + "}");
    }

    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private sealed class StubUsb : IUsbEnumerator
    {
        private readonly List<UsbDeviceEntry> _devices;
        public StubUsb(params UsbDeviceEntry[] devices) => _devices = new List<UsbDeviceEntry>(devices);
        public List<UsbDeviceEntry> Enumerate() => _devices;
    }

    private sealed class ExplodingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("driver actions must not hit the network in these tests");
    }
}
