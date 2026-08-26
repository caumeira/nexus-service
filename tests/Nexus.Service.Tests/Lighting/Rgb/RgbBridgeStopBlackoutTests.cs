using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Media;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>
/// Turning lighting off hard-kills the OpenRGB subprocess, so the final black
/// has to be both awaited and acknowledged first - a fire-and-forget push dies
/// in flight on a slow controller (an ENE DRAM module over SMBus) and the
/// hardware keeps its last colour.
/// </summary>
public class RgbBridgeStopBlackoutTests : IDisposable
{
    private sealed record Op(string Kind, RgbColor[]? Colors);

    private sealed class RecordingController : IRgbController
    {
        private readonly List<Op> _ops = new();
        private readonly object _gate = new();

        public bool IsConnected => true;
        public event Action? DeviceListChanged { add { } remove { } }

        public IReadOnlyList<RgbDevice> Devices { get; set; } = Array.Empty<RgbDevice>();

        public Op[] Ops
        {
            get { lock (_gate) { return _ops.ToArray(); } }
        }

        public void ResetOps()
        {
            lock (_gate) { _ops.Clear(); }
        }

        private void Record(string kind, RgbColor[]? colors = null)
        {
            lock (_gate) { _ops.Add(new Op(kind, colors)); }
        }

        public Task<bool> TryConnectAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task DisconnectAsync() { Record("disconnect"); return Task.CompletedTask; }

        public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default)
        {
            Record("getDevices");
            return Task.FromResult(Devices);
        }

        public Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default) => Task.CompletedTask;

        public Task PushFrameAsync(int deviceIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default)
        {
            Record("push", colors.ToArray());
            return Task.CompletedTask;
        }

        public Task SetOffAsync(int deviceIndex, int ledCount, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushZoneFrameAsync(int deviceIndex, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeZoneAsync(int deviceIndex, int zoneIndex, int newSize, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private readonly string _tempDir;
    private readonly RecordingController _controller = new();
    private readonly RgbBridge _bridge;

    public RgbBridgeStopBlackoutTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-stopblackout-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _controller.Devices = new[]
        {
            new RgbDevice { Index = 0, Name = "DRAM", LedCount = 4 },
        };
        // Nonexistent binary: Start() is a no-op and the bridge still activates.
        _bridge = new RgbBridge(
            new OpenRgbProcessManager(Path.Combine(_tempDir, "missing-openrgb")),
            _controller,
            new LightingEngine(),
            new TestableConfigStore(Path.Combine(_tempDir, "settings.json")),
            new StubUsbEnumerator());
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _bridge.AwaitShutdown();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Confirmed_blackout_pushes_black_then_round_trips_before_returning()
    {
        _bridge.Activate();
        // The initial-rescan hold delays the first committed device list, and
        // the per-physical buffers this blacks out are allocated with it.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline && _bridge.Devices.Count == 0)
        {
            await Task.Delay(50);
        }
        Assert.NotEmpty(_bridge.Devices);
        _controller.ResetOps();

        await _bridge.BlackoutAndConfirmAsync();

        var ops = _controller.Ops;
        var pushes = ops.Where(o => o.Kind == "push").ToArray();
        Assert.NotEmpty(pushes);
        Assert.All(pushes, p => Assert.All(p.Colors!, c => Assert.Equal(default, c)));

        var lastPush = Array.FindLastIndex(ops, o => o.Kind == "push");
        var confirm = Array.FindIndex(ops, lastPush + 1, o => o.Kind == "getDevices");
        Assert.True(confirm > lastPush, "the confirming round-trip must follow every black push");
    }

    [Fact]
    public async Task StopAll_confirms_the_blackout_before_the_subprocess_is_stopped()
    {
        _bridge.Activate();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline && _bridge.Devices.Count == 0)
        {
            await Task.Delay(50);
        }
        Assert.NotEmpty(_bridge.Devices);

        using var gpu = new GpuContext(160, 90);
        using var provider = new LightingProvider(
            new TestableConfigStore(Path.Combine(_tempDir, "provider.json")),
            new LightingEngine(),
            new LightingOutputHub(),
            gpu,
            new MediaLibrary(),
            new Nexus.Service.Platform.DefaultMonitorEnumerator(),
            rgb: _bridge);

        _controller.ResetOps();
        provider.StopAll();

        var ops = _controller.Ops;
        var disconnect = Array.FindIndex(ops, o => o.Kind == "disconnect");
        Assert.True(disconnect >= 0, "StopAll must disconnect");
        var confirm = Array.FindLastIndex(ops, disconnect, o => o.Kind == "getDevices");
        Assert.True(confirm >= 0, "the blackout must be acknowledged before the socket closes");
        var push = Array.FindLastIndex(ops, confirm, o => o.Kind == "push");
        Assert.True(push >= 0, "black must be pushed before the acknowledging round-trip");
    }
}
