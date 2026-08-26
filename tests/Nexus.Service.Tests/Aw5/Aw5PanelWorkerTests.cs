using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Peripherals.Aw5;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Nexus.Service.Models.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Aw5;

/// <summary>
/// The worker's tick order is load-bearing in ways the panel cannot report: it
/// renders nothing and logs nothing when it is wrong, so only these assertions
/// catch it.
/// </summary>
public class Aw5PanelWorkerTests
{
    private const int Vid = 0x3402;

    private static HidDeviceInfo Panel() => new()
    {
        VendorId = Vid, ProductId = 0x0406, UsagePage = 0xFF01,
        OutputReportByteLength = 64, Path = "lp",
    };

    [Fact]
    public async Task Scans_the_bus_on_the_very_first_tick()
    {
        // The rescan counter is seeded at int.MaxValue so the first tick discovers.
        // Testing it with a pre-increment overflows to int.MinValue and the scan
        // never runs: panels stay dark, no error, nothing in the log.
        var hid = new FakeHid(Panel());
        var w = Build(hid, out _);

        await w.TickAsync(CancellationToken.None);

        Assert.True(hid.Finds > 0);
        Assert.True(hid.Device("lp")!.Features.Count > 0);
    }

    [Fact]
    public async Task Does_not_scan_the_bus_on_every_tick()
    {
        // Discovery walks every HID interface on the box; at the panel's keep-alive
        // rate that would sweep the bus once a second forever, on machines with no
        // AW5 too.
        var hid = new FakeHid(Panel());
        var w = Build(hid, out _);

        for (var i = 0; i < 4; i++) await w.TickAsync(CancellationToken.None);

        Assert.Equal(2, hid.Finds / 1);      // one Find per PID, one scan pass
        Assert.True(hid.Device("lp")!.Features.Count >= 4 * 5, "every tick still writes the panel");
    }

    [Fact]
    public async Task Gated_off_never_touches_the_bus()
    {
        // The gate is checked before the scan, so a device the user switched off
        // costs nothing at all.
        var hid = new FakeHid(Panel());
        var w = Build(hid, out var store, gateOff: true);

        await w.TickAsync(CancellationToken.None);
        await w.TickAsync(CancellationToken.None);

        Assert.Equal(0, hid.Finds);
        Assert.Empty(hid.Opens);
    }

    [Fact]
    public async Task Suspending_stops_frames_and_drops_the_handles()
    {
        // A frame landing during the host's USB teardown wedges the panel; see the
        // worker's class doc.
        var hid = new FakeHid(Panel());
        var w = Build(hid, out _);
        await w.TickAsync(CancellationToken.None);
        var framesBefore = hid.Device("lp")!.Features.Count;

        w.OnHostSuspending();
        await w.TickAsync(CancellationToken.None);
        await w.TickAsync(CancellationToken.None);

        Assert.Equal(framesBefore, hid.Device("lp")!.Features.Count);
        Assert.Equal(1, hid.Closes);
    }

    [Fact]
    public async Task Resume_holds_frames_off_until_the_stack_has_settled()
    {
        var hid = new FakeHid(Panel());
        var w = Build(hid, out _);
        await w.TickAsync(CancellationToken.None);
        var framesBefore = hid.Device("lp")!.Features.Count;

        w.OnHostSuspending();
        w.OnHostResumed(settleMs: 60_000);
        await w.TickAsync(CancellationToken.None);

        Assert.Equal(framesBefore, hid.Device("lp")!.Features.Count);
    }

    [Fact]
    public async Task Resume_rediscovers_and_reopens_rather_than_reusing_the_pre_sleep_handle()
    {
        // The devnode survives a suspend, so CloseAbsent alone keeps the old handle.
        var hid = new FakeHid(Panel());
        var w = Build(hid, out _);
        await w.TickAsync(CancellationToken.None);
        var opensBefore = hid.Opens.Count;

        w.OnHostSuspending();
        w.OnHostResumed(settleMs: 0);
        await w.TickAsync(CancellationToken.None);

        Assert.Equal(opensBefore + 1, hid.Opens.Count);
        Assert.True(hid.Device("lp")!.Features.Count > 0);
    }

    [Fact]
    public async Task A_panel_that_keeps_refusing_backs_off_instead_of_reopening_every_tick()
    {
        // Every refused cycle drops and reopens the handle.
        var hid = new FakeHid(Panel()) { SetFeatureResult = false };
        var w = Build(hid, out _);

        for (var i = 0; i < 12; i++) await w.TickAsync(CancellationToken.None);

        Assert.Equal(3, hid.Opens.Count);
    }

    [Fact]
    public async Task A_panel_that_starts_taking_frames_again_leaves_the_backoff()
    {
        var hid = new FakeHid(Panel()) { SetFeatureResult = false };
        var w = Build(hid, out _);
        for (var i = 0; i < 12; i++) await w.TickAsync(CancellationToken.None);

        hid.SetFeatureResult = true;
        for (var i = 0; i < 32; i++) await w.TickAsync(CancellationToken.None);
        var framesAfterRecovery = hid.Device("lp")!.Features.Count;
        await w.TickAsync(CancellationToken.None);

        Assert.True(hid.Device("lp")!.Features.Count > framesAfterRecovery,
            "once a cycle is accepted the panel is written every tick again");
    }

    [Fact]
    public async Task An_unplugged_panel_does_not_come_back_still_serving_its_backoff()
    {
        // Health keyed by path outlives CloseAbsent, so a stale countdown would blank
        // a healthy panel for a full retry window after it returns.
        var hid = new FakeHid(Panel()) { SetFeatureResult = false };
        var w = Build(hid, out _);
        for (var i = 0; i < 12; i++) await w.TickAsync(CancellationToken.None);

        hid.Present = false;
        for (var i = 0; i < 6; i++) await w.TickAsync(CancellationToken.None);
        hid.Present = true;
        hid.SetFeatureResult = true;
        for (var i = 0; i < 6; i++) await w.TickAsync(CancellationToken.None);

        Assert.True(hid.Device("lp")!.Features.Count > 0, "the returning panel is written at once, not after the stale backoff");
    }

    private static Aw5PanelWorker Build(FakeHid hid, out InMemoryConfigStore store, bool gateOff = false)
    {
        store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        if (gateOff) gate.SetEnabled(Aw5Handler.HandlerId, false);
        return new Aw5PanelWorker(new Aw5Hub(hid), new Aw5SensorReader(new FakeSensors()),
            gate);
    }

    private sealed class FakeSensors : ISensorProvider
    {
        public string GetCpuModel() => "TestCPU";
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => new[]
        {
            new HardwareSensor { Id = "c/t", Name = "CPU Package", Type = "Temperature", Value = 44 },
            new HardwareSensor { Id = "c/l", Name = "CPU Total", Type = "Load", Value = 25 },
            new HardwareSensor { Id = "c/clk", Name = "Core Average", Type = "Clock", Value = 3600 },
        };
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
        public IReadOnlyList<string> GetGpuModels() => Array.Empty<string>();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus() => Array.Empty<GpuReadout>();
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => Array.Empty<HardwareSensor>();
        public string GetMemoryTotalFormatted() => "32 GB";
        public string GetRamBrandModel() => "";
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => new Dictionary<string, StorageComponent>();
        public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
        public string GetStorageBrandModel() => "";
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => Array.Empty<HardwareSensor>();
        public string GetMotherboardModel() => "TestMobo";
        public SensorExtras GetSensorExtras() => new();
        public string GetOsVersion() => "TestOS";
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeHid : IHidEnumerator
    {
        private readonly HidDeviceInfo[] _ifaces;
        private readonly Dictionary<string, FakeDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
        public FakeHid(params HidDeviceInfo[] ifaces) { _ifaces = ifaces; }
        public int Finds { get; private set; }
        public bool SetFeatureResult { get; set; } = true;
        public bool Present { get; set; } = true;
        public int Closes { get; private set; }
        public List<string> Opens { get; } = new();
        public FakeDevice? Device(string path) => _devices.TryGetValue(path, out var d) ? d : null;

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId)
        {
            Finds++;
            if (!Present) return Array.Empty<HidDeviceInfo>();
            return _ifaces.Where(i => i.VendorId == vendorId && i.ProductId == productId).ToArray();
        }
        public IReadOnlyList<HidDeviceInfo> FindAll() => _ifaces;
        public IHidDevice? Open(string path, bool forInput = false)
        {
            Opens.Add(path);
            var d = new FakeDevice(path, this);
            _devices[path] = d;
            return d;
        }

        private void NoteClosed() => Closes++;

        internal sealed class FakeDevice : IHidDevice
        {
            private readonly FakeHid _owner;
            public FakeDevice(string path, FakeHid owner) { Path = path; _owner = owner; }
            public List<byte[]> Features { get; } = new();
            public int VendorId => Vid;
            public int ProductId => 0x0406;
            public string Path { get; }
            public string? Serial => null;
            public int UsagePage => 0xFF01;
            public int Usage => 1;
            public bool Write(ReadOnlySpan<byte> r) => true;
            public bool SetFeature(ReadOnlySpan<byte> r)
            {
                if (!_owner.SetFeatureResult) return false;
                Features.Add(r.ToArray());
                return true;
            }
            public bool GetFeature(Span<byte> b) => throw new NotSupportedException();
            public bool GetInputReport(Span<byte> b) => throw new NotSupportedException();
            public bool SetOutputReport(ReadOnlySpan<byte> r) => throw new NotSupportedException();
            public int Read(Span<byte> b, int t) => throw new NotSupportedException();
            public void Dispose() => _owner.NoteClosed();
        }
    }
}
