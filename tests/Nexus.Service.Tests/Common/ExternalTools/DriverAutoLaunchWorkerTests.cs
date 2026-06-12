using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Common.ExternalTools;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Widgets;
using Xunit;

namespace Nexus.Service.Tests.Common.ExternalTools;

public class DriverAutoLaunchWorkerTests : IDisposable
{
    private readonly string _root;
    private readonly string _cache;

    public DriverAutoLaunchWorkerTests()
    {
        var b = Path.Combine(Path.GetTempPath(), "nexus-autolaunch-tests-" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task Does_not_launch_when_no_matching_device()
    {
        WriteDriverApp(withDriverBinary: true);
        var (worker, manager) = Build(new StubUsb()); // empty bus

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.NotEqual(ToolStatus.Running, manager.GetStatus("acme-cooler"));
    }

    [Fact]
    public async Task Launches_present_driver_app_then_single_instances()
    {
        if (OperatingSystem.IsWindows()) return; // real child process; Unix only

        WriteDriverApp(withDriverBinary: true);
        var (worker, manager) = Build(new StubUsb(new UsbDeviceEntry { VendorId = 0x1234, ProductId = 0x0002 }));

        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(ToolStatus.Running, manager.GetStatus("acme-cooler"));

        // A second pass while running is a no-op.
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(ToolStatus.Running, manager.GetStatus("acme-cooler"));

        manager.TerminateAll();
        Assert.NotEqual(ToolStatus.Running, manager.GetStatus("acme-cooler"));
    }

    // ── Harness ──

    private (DriverAutoLaunchWorker worker, ExternalToolManager manager) Build(IUsbEnumerator usb)
    {
        var registry = new AppRegistry(() => new List<AppInstallPaths.Root>
        {
            new(_root, AppInstallPaths.Source.Bundled),
        });
        var manager = new ExternalToolManager(new HttpClient(new ExplodingHandler()), _cache);
        var worker = new DriverAutoLaunchWorker(registry, manager, usb);
        return (worker, manager);
    }

    private void WriteDriverApp(bool withDriverBinary)
    {
        var dir = Path.Combine(_root, "com.example.cooler");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "widget.mjs"), "export default function mount(){}\n");
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            "{\"schema\":\"nexus.app/1\",\"id\":\"com.example.cooler\",\"name\":\"Cooler\",\"version\":\"1.0.0\"," +
            "\"min_nexus_version\":\"0.42.0\",\"runtime\":\"sdk\",\"surfaces\":[\"dashboard\"],\"sizes\":[\"2x2\"]," +
            "\"capabilities\":{},\"driver\":{\"toolId\":\"acme-cooler\",\"match\":{\"vid\":\"1234\",\"pids\":[\"0001\",\"0002\",\"0003\"]}," +
            "\"variants\":{\"0001\":\"VariantA\",\"0002\":\"VariantB\",\"0003\":\"VariantC\"}," +
            "\"manifestUrlBase\":\"https://assets.hellonexus.com/acme_cooler\"," +
            "\"filePattern\":\"MyDriver*.exe\",\"launch\":{\"session\":\"system\",\"hidden\":true}}}");

        if (!withDriverBinary) return;

        var variantDir = Path.Combine(dir, "drivers", "VariantB"); // matches PID 0x0002
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
            => throw new InvalidOperationException("auto-launch must not hit the network in these tests");
    }
}
