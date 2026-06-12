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
        WriteIbpApp(withDriverBinary: true);
        var (worker, manager) = Build(new StubUsb()); // empty bus

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.NotEqual(ToolStatus.Running, manager.GetStatus("ibp-aw5"));
    }

    [Fact]
    public async Task Launches_present_driver_app_then_single_instances()
    {
        if (OperatingSystem.IsWindows()) return; // real child process; Unix only

        WriteIbpApp(withDriverBinary: true);
        var (worker, manager) = Build(new StubUsb(new UsbDeviceEntry { VendorId = 0x3402, ProductId = 0x0406 }));

        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(ToolStatus.Running, manager.GetStatus("ibp-aw5"));

        // A second pass while running is a no-op.
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(ToolStatus.Running, manager.GetStatus("ibp-aw5"));

        manager.TerminateAll();
        Assert.NotEqual(ToolStatus.Running, manager.GetStatus("ibp-aw5"));
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

    private void WriteIbpApp(bool withDriverBinary)
    {
        var dir = Path.Combine(_root, "com.ibuypower.control");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "widget.mjs"), "export default function mount(){}\n");
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            "{\"schema\":\"nexus.app/1\",\"id\":\"com.ibuypower.control\",\"name\":\"iBUYPOWER\",\"version\":\"1.0.0\"," +
            "\"min_nexus_version\":\"0.42.0\",\"runtime\":\"sdk\",\"surfaces\":[\"dashboard\"],\"sizes\":[\"2x2\"]," +
            "\"capabilities\":{},\"driver\":{\"toolId\":\"ibp-aw5\",\"match\":{\"vid\":\"3402\",\"pids\":[\"0405\",\"0406\",\"0407\"]}," +
            "\"variants\":{\"0405\":\"Apaltek\",\"0406\":\"Levelplay\",\"0407\":\"CoolerMaster\"}," +
            "\"manifestUrlBase\":\"https://assets.hellonexus.com/ibp_aw5_aio\"," +
            "\"filePattern\":\"iBUYPOWER_AW5*.exe\",\"launch\":{\"session\":\"system\",\"hidden\":true}}}");

        if (!withDriverBinary) return;

        var variantDir = Path.Combine(dir, "drivers", "Levelplay"); // matches PID 0x0406
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
