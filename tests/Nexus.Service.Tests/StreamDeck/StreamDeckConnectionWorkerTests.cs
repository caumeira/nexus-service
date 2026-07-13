using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Deck;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>Test HID enumerator that opens a caller-supplied device per path.</summary>
internal sealed class FakeWorkerHidEnumerator : IHidEnumerator
{
    public Dictionary<int, List<HidDeviceInfo>> ByProductId { get; } = new();
    public Dictionary<string, IHidDevice> DevicesByPath { get; } = new();

    public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) =>
        ByProductId.TryGetValue(productId, out var list) ? list : Array.Empty<HidDeviceInfo>();

    public IReadOnlyList<HidDeviceInfo> FindAll() => Array.Empty<HidDeviceInfo>();

    public IHidDevice? Open(string path, bool forInput = false) =>
        DevicesByPath.TryGetValue(path, out var dev) ? dev : null;
}

/// <summary>
/// Spy executor: records every dispatch instead of touching real providers.
/// HandleKeyDown fire-and-forgets each dispatch onto the thread pool
/// (Task.Run, never awaited), so two presses within one Tick() can call
/// ExecuteAsync concurrently from different threads - a plain List here would
/// silently drop an entry under that race, so Calls is a ConcurrentQueue.
/// </summary>
internal sealed class FakeDeckActionExecutor : IDeckActionExecutor
{
    public readonly ConcurrentQueue<(DeckAction? Action, string Serial, int KeyIndex, string LatchKey)> Calls = new();
    private readonly Dictionary<string, bool> _latches = new();

    public Task ExecuteAsync(DeckAction? action, string serial, int keyIndex, string latchKey, CancellationToken ct)
    {
        Calls.Enqueue((action, serial, keyIndex, latchKey));
        return Task.CompletedTask;
    }

    public bool IsToggleOn(DeckToggleState? state, string latchKey) => _latches.TryGetValue(latchKey, out var v) && v;
}

/// <summary>Minimal settable ISensorProvider for monitoring-tile push tests.</summary>
internal sealed class FakeSensorProvider : ISensorProvider
{
    public IReadOnlyList<HardwareSensor> CpuSensors { get; set; } = Array.Empty<HardwareSensor>();
    public IReadOnlyList<GpuReadout> Gpus { get; set; } = Array.Empty<GpuReadout>();
    public IReadOnlyList<HardwareSensor> MemorySensors { get; set; } = Array.Empty<HardwareSensor>();
    public IReadOnlyList<HardwareSensor> MotherboardSensors { get; set; } = Array.Empty<HardwareSensor>();
    public IReadOnlyDictionary<string, StorageComponent> StorageComponents { get; set; } = new Dictionary<string, StorageComponent>();

    public string GetCpuModel() => "TestCPU";
    public IReadOnlyList<HardwareSensor> GetCpuSensors() => CpuSensors;
    public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
    public IReadOnlyList<string> GetGpuModels() => Array.Empty<string>();
    public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
    public IReadOnlyList<GpuReadout> GetGpus() => Gpus;
    public IReadOnlyList<HardwareSensor> GetMemorySensors() => MemorySensors;
    public string GetMemoryTotalFormatted() => "32 GB";
    public string GetRamBrandModel() => "";
    public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => StorageComponents;
    public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
    public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
    public string GetStorageBrandModel() => "";
    public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => MotherboardSensors;
    public string GetMotherboardModel() => "TestMobo";
    public SensorExtras GetSensorExtras() => new();
    public string GetOsVersion() => "TestOS";
    public void SetPollingRate(int pollingRate) { }
    public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public class StreamDeckConnectionWorkerTests
{
    private static readonly StreamDeckModel Mini = StreamDeckModels.ByProductId(0x0063)!;

    private sealed record Fixtures(
        FakeWorkerHidEnumerator Hid,
        HardwarePresence Presence,
        DeviceControlGate Gate,
        InMemoryConfigStore Store,
        FakeDeckActionExecutor Executor,
        StreamDeckImageCache ImageCache,
        MultiplexHub Hub,
        FakeSensorProvider Sensors);

    private static Fixtures NewFixtures(bool devicePresent)
    {
        var hid = new FakeWorkerHidEnumerator();
        var usbEntries = devicePresent
            ? new List<UsbDeviceEntry> { new() { VendorId = StreamDeckModels.VendorId, ProductId = Mini.ProductId } }
            : new List<UsbDeviceEntry>();
        var presence = new HardwarePresence(new FixedUsbEnumerator(usbEntries.ToArray()));
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        // DeviceControlPolicy defaults "streamdeck" off (Elgato's own software
        // is a mapped competitor - see DeviceControlPolicyTests); these tests
        // exercise the worker's own connect/dispatch behavior, so opt in
        // explicitly rather than depending on the brand default.
        gate.SetEnabled("streamdeck", true);
        var imageCache = new StreamDeckImageCache(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexus-streamdeck-test-" + Guid.NewGuid().ToString("N")));
        return new Fixtures(hid, presence, gate, store, new FakeDeckActionExecutor(), imageCache, new MultiplexHub(), new FakeSensorProvider());
    }

    private static StreamDeckConnectionWorker NewWorker(Fixtures f, SimulatedStreamDeckSurface? simulated = null) =>
        new(f.Hid, f.Presence, f.Gate, f.Store, f.Executor, f.ImageCache, f.Hub, f.Sensors, simulated);

    private static void AddMiniDevice(FakeWorkerHidEnumerator hid, string path, string serial)
    {
        hid.ByProductId[Mini.ProductId] = new List<HidDeviceInfo>
        {
            new() { VendorId = StreamDeckModels.VendorId, ProductId = Mini.ProductId, Path = path, Serial = serial },
        };
        hid.DevicesByPath[path] = new MockStreamDeckHidDevice { Serial = serial, ProductId = Mini.ProductId, Path = path };
    }

    [Fact]
    public void Tick_DiscoversAndConnectsAPresentDeck()
    {
        var f = NewFixtures(devicePresent: true);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        using var worker = NewWorker(f);

        worker.Tick();

        Assert.Single(worker.Surfaces);
        Assert.NotNull(worker.FindBySerial("SERIAL-1"));
        Assert.True(worker.FindBySerial("SERIAL-1")!.IsConnected);
    }

    [Fact]
    public void Tick_DiscoversAndConnectsAGen2Deck()
    {
        var xl = StreamDeckModels.ByProductId(0x006c)!;
        var f = NewFixtures(devicePresent: true);
        f.Hid.ByProductId[xl.ProductId] = new List<HidDeviceInfo>
        {
            new() { VendorId = StreamDeckModels.VendorId, ProductId = xl.ProductId, Path = "path-xl", Serial = "XL-SERIAL" },
        };
        f.Hid.DevicesByPath["path-xl"] = new MockStreamDeckHidDevice { Serial = "XL-SERIAL", ProductId = xl.ProductId, Path = "path-xl" };
        using var worker = NewWorker(f);

        worker.Tick();

        Assert.NotNull(worker.FindBySerial("XL-SERIAL"));
        Assert.True(worker.FindBySerial("XL-SERIAL")!.IsConnected);
    }

    [Fact]
    public void Tick_GateDisabled_NeverConnects()
    {
        var f = NewFixtures(devicePresent: true);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        f.Gate.SetEnabled("streamdeck", false);
        using var worker = NewWorker(f);

        worker.Tick();

        Assert.Empty(worker.Surfaces);
    }

    [Fact]
    public void Tick_NoUsbPresence_SkipsHidScanEntirely()
    {
        var f = NewFixtures(devicePresent: false);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1"); // hid would find it, but presence says no 0x0FD9 on the bus
        using var worker = NewWorker(f);

        worker.Tick();

        Assert.Empty(worker.Surfaces);
    }

    [Fact]
    public void Tick_DeviceUnplugged_DisposesAndRemovesTheSurface()
    {
        var f = NewFixtures(devicePresent: false);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        var usb = new MutableUsbEnumerator();
        usb.Devices.Add(new UsbDeviceEntry { VendorId = StreamDeckModels.VendorId, ProductId = Mini.ProductId });
        var presence = new HardwarePresence(usb);
        using var worker = new StreamDeckConnectionWorker(f.Hid, presence, f.Gate, f.Store, f.Executor, f.ImageCache, f.Hub, f.Sensors);

        worker.Tick();
        var dev = (MockStreamDeckHidDevice)f.Hid.DevicesByPath["path-1"];
        Assert.Single(worker.Surfaces);

        usb.Devices.Clear();
        f.Hid.ByProductId.Clear();
        worker.Tick();

        Assert.Empty(worker.Surfaces);
        Assert.True(dev.Disposed);
    }

    [Fact]
    public void Tick_DeviceUnplugged_PersistedProductIdSurvivesForTheDisconnectedDecksListing()
    {
        var f = NewFixtures(devicePresent: false);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        var usb = new MutableUsbEnumerator();
        usb.Devices.Add(new UsbDeviceEntry { VendorId = StreamDeckModels.VendorId, ProductId = Mini.ProductId });
        var presence = new HardwarePresence(usb);
        using var worker = new StreamDeckConnectionWorker(f.Hid, presence, f.Gate, f.Store, f.Executor, f.ImageCache, f.Hub, f.Sensors);

        worker.Tick();
        Assert.Equal(Mini.ProductId, f.Store.Load().StreamDeck.Decks["SERIAL-1"].ProductId);

        usb.Devices.Clear();
        f.Hid.ByProductId.Clear();
        worker.Tick();

        Assert.Empty(worker.Surfaces);
        Assert.Equal(Mini.ProductId, f.Store.Load().StreamDeck.Decks["SERIAL-1"].ProductId);
    }

    [Fact]
    public void Tick_WithSimulatedSurface_RegistersItOnFirstTick()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        using var worker = NewWorker(f, simulated);

        worker.Tick();

        Assert.NotNull(worker.FindBySerial("sim-0001"));
    }

    [Fact]
    public void Tick_WithSimulatedSurface_PumpsPressesAcrossMultipleTicksWithoutError()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        simulated.Poke(0, true);
        worker.Tick();
        simulated.Poke(0, false);
        worker.Tick();

        Assert.True(worker.FindBySerial("sim-0001")!.IsConnected);
    }

    [Fact]
    public void Tick_GateDisabled_DoesNotDisposeTheSimulatedSurface()
    {
        // The simulated surface is a shared DI singleton the dev routes also
        // hold a reference to; disabling Nexus Control must stop tracking it,
        // not dispose the shared instance out from under those routes.
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        f.Gate.SetEnabled("streamdeck", false);
        worker.Tick();

        Assert.True(simulated.IsConnected);
    }

    [Fact]
    public async Task SimulatedPress_OnRootActionSlot_DispatchesToTheExecutor()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var action = new DeckAction { Type = "openUrl", Url = "https://example.com" };
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        simulated.Poke(0, true);
        worker.Tick();
        if (worker.LastDispatchTask is not null)
        {
            await worker.LastDispatchTask;
        }

        var call = Assert.Single(f.Executor.Calls);
        Assert.Equal("sim-0001", call.Serial);
        Assert.Equal(0, call.KeyIndex);
        Assert.Same(action, call.Action);
    }

    [Fact]
    public async Task SimulatedPress_FastDownThenUpWithinOneTick_StillDispatchesExactlyOnce()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var action = new DeckAction { Type = "openUrl", Url = "https://example.com" };
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        // Both transitions queue before a single Tick drains them, mirroring
        // a press+release faster than the 1s tick (the sub-tick hold that
        // previously got dropped when ReadInput only sampled the latest state).
        simulated.Poke(0, true);
        simulated.Poke(0, false);
        worker.Tick();
        if (worker.LastDispatchTask is not null)
        {
            await worker.LastDispatchTask;
        }

        var call = Assert.Single(f.Executor.Calls);
        Assert.Equal("sim-0001", call.Serial);
        Assert.Equal(0, call.KeyIndex);
        Assert.Same(action, call.Action);
    }

    [Fact]
    public async Task RealSurface_DedicatedReaderDrainsQueuedReportsAndDispatchesEachPress()
    {
        var f = NewFixtures(devicePresent: true);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        var actionA = new DeckAction { Type = "openUrl", Url = "https://a.example.com" };
        var actionB = new DeckAction { Type = "openUrl", Url = "https://b.example.com" };
        f.Store.Update(s => s.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = actionA }, new DeckSlot { Action = actionB } } } } },
        });
        using var worker = NewWorker(f);
        worker.Tick(); // connects the surface and starts its dedicated StreamDeckInputReader

        // Two full press+release cycles queued as 4 separate HID reports. The
        // reader's background thread (not this test's Tick calls) is what
        // drains and decodes them, exactly as a real burst of rapid presses
        // arriving between ticks would.
        var device = (MockStreamDeckHidDevice)f.Hid.DevicesByPath["path-1"];
        device.PendingReads.Enqueue(new byte[] { 0x01, 1, 0, 0, 0, 0, 0 }); // key 0 down
        device.PendingReads.Enqueue(new byte[] { 0x01, 0, 0, 0, 0, 0, 0 }); // key 0 up
        device.PendingReads.Enqueue(new byte[] { 0x01, 0, 1, 0, 0, 0, 0 }); // key 1 down
        device.PendingReads.Enqueue(new byte[] { 0x01, 0, 0, 0, 0, 0, 0 }); // key 1 up

        for (var i = 0; i < 50 && f.Executor.Calls.Count < 2; i++)
        {
            await Task.Delay(10);
        }

        // Both dispatches are fire-and-forget Task.Run work items (HandleKeyDown
        // never awaits them), so their thread-pool execution order relative to
        // each other isn't guaranteed - assert the set, not indexed positions.
        // Exactly 2 (not 4) confirms the up transitions were diffed out, not dispatched.
        Assert.Equal(2, f.Executor.Calls.Count);
        Assert.Contains(f.Executor.Calls, c => c.KeyIndex == 0 && ReferenceEquals(c.Action, actionA));
        Assert.Contains(f.Executor.Calls, c => c.KeyIndex == 1 && ReferenceEquals(c.Action, actionB));
    }

    [Fact]
    public async Task RealSurface_DedicatedReaderDrivesFolderNavPushAndBack()
    {
        var f = NewFixtures(devicePresent: true);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        var innerAction = new DeckAction { Type = "openUrl", Url = "https://inner.example.com" };
        f.Store.Update(s => s.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages = { new DeckPage { Slots = { new DeckSlot { Folder = new DeckFolder { Slots = { new DeckSlot { Action = innerAction } } } } } } },
            },
        });
        using var worker = NewWorker(f);
        worker.Tick();
        var device = (MockStreamDeckHidDevice)f.Hid.DevicesByPath["path-1"];

        // Press key 0 (the folder slot) at root - pushes into the folder.
        device.PendingReads.Enqueue(new byte[] { 0x01, 1, 0, 0, 0, 0, 0 });
        device.PendingReads.Enqueue(new byte[] { 0x01, 0, 0, 0, 0, 0, 0 });
        for (var i = 0; i < 50 && worker.GetFolderPath("SERIAL-1").Count == 0; i++)
        {
            await Task.Delay(10);
        }
        Assert.Equal(new[] { 0 }, worker.GetFolderPath("SERIAL-1"));

        // Inside the folder, key 0 is reserved for Back; the folder's own
        // slot 0 lives at physical key 1.
        device.PendingReads.Enqueue(new byte[] { 0x01, 0, 1, 0, 0, 0, 0 });
        device.PendingReads.Enqueue(new byte[] { 0x01, 0, 0, 0, 0, 0, 0 });
        for (var i = 0; i < 50 && f.Executor.Calls.Count < 1; i++)
        {
            await Task.Delay(10);
        }
        var call = Assert.Single(f.Executor.Calls);
        Assert.Same(innerAction, call.Action);
        Assert.Equal(0, call.KeyIndex);

        // Pressing key 0 again (now Back) pops the folder path.
        device.PendingReads.Enqueue(new byte[] { 0x01, 1, 0, 0, 0, 0, 0 });
        device.PendingReads.Enqueue(new byte[] { 0x01, 0, 0, 0, 0, 0, 0 });
        for (var i = 0; i < 50 && worker.GetFolderPath("SERIAL-1").Count > 0; i++)
        {
            await Task.Delay(10);
        }
        Assert.Empty(worker.GetFolderPath("SERIAL-1"));
    }

    [Fact]
    public async Task KeyPress_OnFolderSlot_PushesFolderAndShiftsSubsequentKeysByOne()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var innerAction = new DeckAction { Type = "openUrl", Url = "https://inner.example.com" };
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage
                    {
                        Slots = { new DeckSlot { Folder = new DeckFolder { Slots = { new DeckSlot { Action = innerAction } } } } },
                    },
                },
            },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        // Press key 0 (the folder slot) at root - no shift applies at root.
        simulated.Poke(0, true);
        worker.Tick();
        simulated.Poke(0, false);
        worker.Tick();

        Assert.Equal(new[] { 0 }, worker.GetFolderPath("sim-0001"));
        Assert.Empty(f.Executor.Calls);

        // Inside the folder, key 0 is reserved for Back; the folder's own
        // slot 0 lives at physical key 1.
        simulated.Poke(1, true);
        worker.Tick();
        if (worker.LastDispatchTask is not null)
        {
            await worker.LastDispatchTask;
        }

        var call = Assert.Single(f.Executor.Calls);
        Assert.Same(innerAction, call.Action);
        // The dispatch's key index is the logical slot index within the
        // folder's own slot list (0), not the physical key number (1) - the
        // physical key was shifted by the reserved Back key at index 0.
        Assert.Equal(0, call.KeyIndex);
    }

    [Fact]
    public void KeyPress_OnBackKey_PopsTheFolderPath()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages = { new DeckPage { Slots = { new DeckSlot { Folder = new DeckFolder { Slots = { new DeckSlot() } } } } } },
            },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        simulated.Poke(0, true);
        worker.Tick();
        simulated.Poke(0, false);
        worker.Tick();
        Assert.Equal(new[] { 0 }, worker.GetFolderPath("sim-0001"));

        simulated.Poke(0, true);
        worker.Tick();

        Assert.Empty(worker.GetFolderPath("sim-0001"));
    }

    [Fact]
    public void FolderView_PushesTheCachedBackBitmapAtKeyZero_FallsBackToClearWhenUncached()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages = { new DeckPage { Slots = { new DeckSlot { Folder = new DeckFolder { Slots = { new DeckSlot() } } } } } },
            },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        simulated.Poke(0, true);
        worker.Tick();
        simulated.Poke(0, false);
        worker.Tick();
        Assert.Equal(new[] { 0 }, worker.GetFolderPath("sim-0001"));
        Assert.Null(simulated.PeekKeyImage(0));

        var bytes = new byte[] { 7, 7, 7 };
        var hash = StreamDeckImageCache.Hash(bytes);
        f.ImageCache.Store("sim-0001", hash, bytes);
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"].ImageRefs["back/0"] = hash);
        worker.RefreshView("sim-0001");

        Assert.Equal(bytes, simulated.PeekKeyImage(0));
    }

    [Theory]
    [InlineData(0x0086, 1, 3, 3)]  // Pedal
    [InlineData(0x0063, 2, 3, 6)]  // Mini
    [InlineData(0x0090, 2, 3, 6)]  // Mini MK.2
    [InlineData(0x006c, 4, 8, 32)] // XL
    public void SetSimulatedModel_ForAGivenModel_RegistersASurfaceWithMatchingLayout(
        int productId, int rows, int columns, int keyCount)
    {
        var f = NewFixtures(devicePresent: false);
        using var worker = NewWorker(f);

        Assert.True(worker.SetSimulatedModel(productId));

        var surface = Assert.Single(worker.Surfaces).Value;
        Assert.Equal(rows, surface.Model.Rows);
        Assert.Equal(columns, surface.Model.Columns);
        Assert.Equal(keyCount, surface.Model.KeyCount);
    }

    [Fact]
    public void SetSimulatedModel_UnknownProductId_ReturnsFalseAndAddsNoSurface()
    {
        var f = NewFixtures(devicePresent: false);
        using var worker = NewWorker(f);

        Assert.False(worker.SetSimulatedModel(0xDEAD));

        Assert.Empty(worker.Surfaces);
    }

    [Fact]
    public void SetSimulatedModel_ReplacingTheCurrentModel_TearsDownTheOldSurfaceCleanly()
    {
        var f = NewFixtures(devicePresent: false);
        using var worker = NewWorker(f);
        Assert.True(worker.SetSimulatedModel(0x0063)); // Mini
        var miniSerial = Assert.Single(worker.Surfaces).Value.Serial;
        Assert.True(worker.HasSerialState(miniSerial));

        Assert.True(worker.SetSimulatedModel(0x006c)); // XL

        Assert.False(worker.HasSerialState(miniSerial));
        Assert.Null(worker.FindBySerial(miniSerial));
        var xlSurface = Assert.Single(worker.Surfaces).Value;
        Assert.Equal("XL", xlSurface.Model.Name);
    }

    [Fact]
    public void ClearSimulatedModel_RemovesTheSurfaceAndItsState()
    {
        var f = NewFixtures(devicePresent: false);
        using var worker = NewWorker(f);
        Assert.True(worker.SetSimulatedModel(0x0063));
        var serial = Assert.Single(worker.Surfaces).Value.Serial;

        worker.ClearSimulatedModel();

        Assert.Empty(worker.Surfaces);
        Assert.False(worker.HasSerialState(serial));
    }

    [Fact]
    public void ClearSimulatedModel_WithNoSimulatedDeck_IsANoOp()
    {
        var f = NewFixtures(devicePresent: false);
        using var worker = NewWorker(f);

        worker.ClearSimulatedModel();

        Assert.Empty(worker.Surfaces);
    }

    [Fact]
    public async Task SetSimulatedModel_ThenPoke_DispatchesThroughTheExecutorLikeAnyOtherSurface()
    {
        var f = NewFixtures(devicePresent: false);
        using var worker = NewWorker(f);
        Assert.True(worker.SetSimulatedModel(0x0063)); // Mini
        var serial = Assert.Single(worker.Surfaces).Value.Serial;
        var action = new DeckAction { Type = "openUrl", Url = "https://example.com" };
        f.Store.Update(s => s.StreamDeck.Decks[serial] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });

        var simulated = (SimulatedStreamDeckSurface)worker.Surfaces[StreamDeckConnectionWorker.SimulatedKey];
        simulated.Poke(0, true);
        worker.Tick();
        if (worker.LastDispatchTask is not null)
        {
            await worker.LastDispatchTask;
        }

        var call = Assert.Single(f.Executor.Calls);
        Assert.Equal(serial, call.Serial);
        Assert.Equal(0, call.KeyIndex);
        Assert.Same(action, call.Action);
    }

    /// <summary>
    /// Two-page config for page-nav tests: each page's slot 1 dispatches a
    /// distinguishable openUrl action, so a dispatch after a page change
    /// proves the worker actually resolved the NEW page's view, not just
    /// advanced a counter.
    /// </summary>
    private static PhysicalDeckSettings TwoPageDeckSettings() => new()
    {
        Deck = new DeckConfig
        {
            Pages =
            {
                new DeckPage
                {
                    Slots =
                    {
                        new DeckSlot { Action = new DeckAction { Type = "page", Op = "next" } },
                        new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://page0.example.com" } },
                        new DeckSlot { Action = new DeckAction { Type = "page", Op = "prev" } },
                    },
                },
                new DeckPage
                {
                    Slots =
                    {
                        new DeckSlot { Action = new DeckAction { Type = "page", Op = "prev" } },
                        new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://page1.example.com" } },
                        new DeckSlot { Action = new DeckAction { Type = "page", Op = "goto", Target = 0 } },
                    },
                },
            },
        },
    };

    [Fact]
    public async Task PageNextAction_AdvancesPageAndDispatchesFromTheNewPagesView()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = TwoPageDeckSettings());
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        simulated.Poke(0, true);
        worker.Tick();
        simulated.Poke(0, false);
        worker.Tick();

        Assert.Equal(1, worker.GetCurrentPage("sim-0001"));
        Assert.Empty(f.Executor.Calls);

        simulated.Poke(1, true);
        worker.Tick();
        if (worker.LastDispatchTask is not null)
        {
            await worker.LastDispatchTask;
        }

        var call = Assert.Single(f.Executor.Calls);
        Assert.Equal("https://page1.example.com", call.Action!.Url);
    }

    [Fact]
    public void PagePrevAction_AtFirstPage_ClampsAndStaysAtZero()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = TwoPageDeckSettings());
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        simulated.Poke(2, true);
        worker.Tick();

        Assert.Equal(0, worker.GetCurrentPage("sim-0001"));
    }

    [Fact]
    public void PageGotoAction_JumpsToTheTargetPageClampedToRange()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var settings = TwoPageDeckSettings();
        // Out-of-range goto target must clamp to the last real page (index 1).
        settings.Deck.Pages[0].Slots[1].Action = new DeckAction { Type = "page", Op = "goto", Target = 99 };
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = settings);
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        simulated.Poke(1, true);
        worker.Tick();
        Assert.Equal(1, worker.GetCurrentPage("sim-0001"));

        simulated.Poke(1, false);
        worker.Tick();
        simulated.Poke(2, true); // page 1's slot 2: goto target=0
        worker.Tick();

        Assert.Equal(0, worker.GetCurrentPage("sim-0001"));
    }

    [Fact]
    public void PageIndicatorSlot_PressDoesNotDispatchToTheExecutor()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "pageIndicator" } } } } } },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        simulated.Poke(0, true);
        worker.Tick();

        Assert.Empty(f.Executor.Calls);
    }

    [Fact]
    public void PageAction_InsideAFolder_ResetsFolderPathToRoot()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot
                            {
                                Folder = new DeckFolder
                                {
                                    Slots = { new DeckSlot { Action = new DeckAction { Type = "page", Op = "next" } } },
                                },
                            },
                        },
                    },
                    new DeckPage(),
                },
            },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        // Enter the folder (key 0), then press the folder's own slot 0 (physical
        // key 1, since key 0 is reserved as Back inside the folder) - the "page
        // next" action.
        simulated.Poke(0, true);
        worker.Tick();
        simulated.Poke(0, false);
        worker.Tick();
        Assert.Equal(new[] { 0 }, worker.GetFolderPath("sim-0001"));

        simulated.Poke(1, true);
        worker.Tick();

        Assert.Equal(1, worker.GetCurrentPage("sim-0001"));
        Assert.Empty(worker.GetFolderPath("sim-0001"));
    }

    [Fact]
    public void GetCurrentPage_UnknownSerial_ReturnsZero()
    {
        var f = NewFixtures(devicePresent: false);
        using var worker = NewWorker(f);

        Assert.Equal(0, worker.GetCurrentPage("never-connected"));
    }

    /// <summary>
    /// Physical push-in feedback (Elgato-software parity): key-down pushes a
    /// scaled-and-inset variant of the key's uploaded image immediately, and
    /// key-up restores the exact original bytes.
    /// </summary>
    [Fact]
    public void HandleKeyDown_LeafActionWithUploadedImage_PushesAPressedVariant_KeyUpRestoresTheOriginal()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var original = StreamDeckProtocol.BuildBlankBmp(Mini.KeyPixelSize);
        var hash = StreamDeckImageCache.Hash(original);
        f.ImageCache.Store("sim-0001", hash, original);
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://example.com" } } } } } },
            ImageRefs = { ["0.0/0"] = hash },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();
        Assert.Equal(original, simulated.PeekKeyImage(0));

        simulated.Poke(0, true);
        worker.Tick();
        var pressed = simulated.PeekKeyImage(0);
        Assert.NotNull(pressed);
        Assert.NotEqual(original, pressed);
        // Same wire dimensions (re-encoded at the source's own size), so the
        // difference is pixel content (the background inset), not a resize
        // that would desync from the model's fixed wire image length.
        Assert.Equal(original.Length, pressed!.Length);

        simulated.Poke(0, false);
        worker.Tick();
        Assert.Equal(original, simulated.PeekKeyImage(0));
    }

    [Fact]
    public void HandleKeyDown_TwoSlotsSharingTheSameUploadedImage_ProduceByteIdenticalPressedVariants()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var original = StreamDeckProtocol.BuildBlankBmp(Mini.KeyPixelSize);
        var hash = StreamDeckImageCache.Hash(original);
        f.ImageCache.Store("sim-0001", hash, original);
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://a.example.com" } },
                            new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://b.example.com" } },
                        },
                    },
                },
            },
            ImageRefs = { ["0.0/0"] = hash, ["0.1/0"] = hash },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        simulated.Poke(0, true);
        worker.Tick();
        var pressedA = simulated.PeekKeyImage(0);
        simulated.Poke(0, false);
        worker.Tick();

        simulated.Poke(1, true);
        worker.Tick();
        var pressedB = simulated.PeekKeyImage(1);

        Assert.NotNull(pressedA);
        Assert.Equal(pressedA, pressedB);
    }

    /// <summary>
    /// A monitoring key now gets the same push-in feedback a leaf action
    /// gets, rendered from its own last-pushed tile. Key-up must restore the
    /// original tile immediately (its own explicit SetKeyImage call), not
    /// merely rely on the same tick's later RefreshMonitoringKeys pass -
    /// asserted by requiring two separate SetKeyImage calls on release
    /// (the explicit restore, plus the normal repaint ForceMonitoringKeyRefresh
    /// still triggers), not one.
    /// </summary>
    [Fact]
    public void HandleKeyDown_MonitoringSlot_PushesAPressedVariant_KeyUpRestoresTheOriginalTileImmediately()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();
        var original = simulated.PeekKeyImage(0);
        Assert.NotNull(original);

        simulated.Poke(0, true);
        worker.Tick();
        var pressed = simulated.PeekKeyImage(0);
        Assert.NotNull(pressed);
        Assert.NotEqual(original, pressed);

        var callsBeforeRelease = simulated.SetKeyImageCallCount;
        simulated.Poke(0, false);
        worker.Tick();

        Assert.Equal(2, simulated.SetKeyImageCallCount - callsBeforeRelease);
        Assert.Equal(original, simulated.PeekKeyImage(0));
    }

    /// <summary>
    /// ForceMonitoringKeyRefresh (called from HandleKeyUp) drops only the
    /// tracked hash, not the bytes, precisely so a re-press before the next
    /// tick's repaint still has something to render an inset from. All three
    /// transitions land in one Tick() call (SimulatedStreamDeckSurface drains
    /// its whole queue), so RefreshMonitoringKeys never runs in between to
    /// repopulate the hash on its own.
    /// </summary>
    [Fact]
    public void HandleKeyDown_MonitoringSlot_RepressedBeforeTheNextTick_StillShowsAPressedInset()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();
        var original = simulated.PeekKeyImage(0);

        simulated.Poke(0, true);
        simulated.Poke(0, false);
        simulated.Poke(0, true);
        worker.Tick();

        var pressedAgain = simulated.PeekKeyImage(0);
        Assert.NotNull(pressedAgain);
        Assert.NotEqual(original, pressedAgain);
    }

    /// <summary>
    /// A monitoring key never rendered yet has nothing in
    /// _monitoringLastPushedBytes for HandlePressVisual to render an inset
    /// from - skipped silently, no exception, no SetKeyImage call. The
    /// config is added to the store AFTER the connect tick (bypassing
    /// PushCurrentView/RefreshView, which would otherwise render it inline)
    /// so HandleKeyDown resolves a real monitoring slot whose tile genuinely
    /// has never been painted. RefreshMonitoringKeys, later in the same
    /// tick, would normally paint it first-time, but the key is already
    /// marked held by then, so it skips too - proving the guard, not a race.
    /// </summary>
    [Fact]
    public void HandleKeyDown_MonitoringSlotNeverRendered_SkipsSilentlyWithNoPressedImage()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        using var worker = NewWorker(f, simulated);
        worker.Tick(); // connects with no persisted deck config - root view stays empty
        Assert.Null(simulated.PeekKeyImage(0));
        var callsBeforeHold = simulated.SetKeyImageCallCount;

        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages = { new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } } } } },
            },
        });

        var exception = Record.Exception(() =>
        {
            simulated.Poke(0, true);
            worker.Tick();
        });

        Assert.Null(exception);
        Assert.Equal(callsBeforeHold, simulated.SetKeyImageCallCount);
        Assert.Null(simulated.PeekKeyImage(0));
    }

    /// <summary>
    /// Image-refs v2: the key is page-qualified, so two pages that each use
    /// slot 0 at their own root resolve their own distinct uploaded image
    /// instead of one page's upload overwriting the other's.
    /// </summary>
    [Fact]
    public void Tick_TwoPagesReuseTheSameSlotIndex_EachPageResolvesItsOwnUploadedImage()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var page0Bytes = new byte[] { 1, 1, 1 };
        var page1Bytes = new byte[] { 2, 2, 2 };
        var page0Hash = StreamDeckImageCache.Hash(page0Bytes);
        var page1Hash = StreamDeckImageCache.Hash(page1Bytes);
        f.ImageCache.Store("sim-0001", page0Hash, page0Bytes);
        f.ImageCache.Store("sim-0001", page1Hash, page1Bytes);
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://page0.example.com" } } } },
                    new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://page1.example.com" } } } },
                },
            },
            ImageRefs = { ["0.0/0"] = page0Hash, ["1.0/0"] = page1Hash },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();
        Assert.Equal(page0Bytes, simulated.PeekKeyImage(0));

        Assert.True(worker.SetNav("sim-0001", 1, Array.Empty<int>()));

        Assert.Equal(page1Bytes, simulated.PeekKeyImage(0));
    }

    /// <summary>
    /// A legacy pre-v2 key ("0/0", no leading page segment) is an orphan
    /// under image-refs v2 - ResolveSlotImage only builds page-qualified
    /// keys, so it never resolves; no migration re-keys it.
    /// </summary>
    [Fact]
    public void Tick_LegacyUnqualifiedImageRefKey_NeverResolves()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var bytes = StreamDeckProtocol.BuildBlankBmp(Mini.KeyPixelSize);
        var hash = StreamDeckImageCache.Hash(bytes);
        f.ImageCache.Store("sim-0001", hash, bytes);
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://example.com" } } } } } },
            ImageRefs = { ["0/0"] = hash },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();

        Assert.Null(simulated.PeekKeyImage(0));
    }

    [Fact]
    public void HandleKeyDown_OnAPageNavKey_MatchesAPureNavRepaintWithNoExtraPressedPush()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = TwoPageDeckSettings());
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        var beforeNav = simulated.SetKeyImageCallCount;
        worker.SetNav("sim-0001", 1, Array.Empty<int>());
        var navOnlyDelta = simulated.SetKeyImageCallCount - beforeNav;
        var navOnlyImages = SnapshotKeyImages(simulated, Mini.KeyCount);

        worker.SetNav("sim-0001", 0, Array.Empty<int>());

        var beforePress = simulated.SetKeyImageCallCount;
        simulated.Poke(0, true); // page 0 slot 0 is "page next" (TwoPageDeckSettings)
        worker.Tick();
        var pressDelta = simulated.SetKeyImageCallCount - beforePress;
        var pressImages = SnapshotKeyImages(simulated, Mini.KeyCount);

        Assert.Equal(1, worker.GetCurrentPage("sim-0001"));
        Assert.Equal(navOnlyDelta, pressDelta);
        Assert.Equal(navOnlyImages, pressImages);
    }

    [Fact]
    public void HandleKeyDown_OnAFolderSlot_MatchesAPureNavRepaintWithNoExtraPressedPush()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages = { new DeckPage { Slots = { new DeckSlot { Folder = new DeckFolder { Slots = { new DeckSlot() } } } } } },
            },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        var beforeNav = simulated.SetKeyImageCallCount;
        worker.SetNav("sim-0001", 0, new[] { 0 });
        var navOnlyDelta = simulated.SetKeyImageCallCount - beforeNav;
        var navOnlyImages = SnapshotKeyImages(simulated, Mini.KeyCount);

        worker.SetNav("sim-0001", 0, Array.Empty<int>());

        var beforePress = simulated.SetKeyImageCallCount;
        simulated.Poke(0, true); // the only root slot: a folder
        worker.Tick();
        var pressDelta = simulated.SetKeyImageCallCount - beforePress;
        var pressImages = SnapshotKeyImages(simulated, Mini.KeyCount);

        Assert.Equal(new[] { 0 }, worker.GetFolderPath("sim-0001"));
        Assert.Equal(navOnlyDelta, pressDelta);
        Assert.Equal(navOnlyImages, pressImages);
    }

    private static byte[]?[] SnapshotKeyImages(SimulatedStreamDeckSurface surface, int keyCount)
    {
        var snapshot = new byte[]?[keyCount];
        for (var i = 0; i < keyCount; i++)
        {
            snapshot[i] = surface.PeekKeyImage(i);
        }
        return snapshot;
    }

    private static bool ImageEquals(byte[]? a, byte[]? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        return a.AsSpan().SequenceEqual(b);
    }

    [Fact]
    public void Tick_MonitoringSlot_RendersAndPushesAValidWireImage_WithoutTouchingImageRefsOrTheCache()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "line" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();

        var bytes = simulated.PeekKeyImage(0);
        Assert.NotNull(bytes);
        Assert.True(Mini.IsValidWireImageLength(bytes!.Length));
        Assert.Empty(f.Store.Load().StreamDeck.Decks["sim-0001"].ImageRefs);
    }

    [Fact]
    public void Tick_MonitoringSlot_UnchangedSensorValue_SkipsTheSecondPush()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);

        // Connect drives PushCurrentView's two-pass repaint for the one
        // monitoring slot: an empty placeholder, then the real tile.
        worker.Tick();
        Assert.Equal(2, simulated.SetKeyImageCallCount);

        worker.Tick();
        worker.Tick();

        Assert.Equal(2, simulated.SetKeyImageCallCount);
    }

    /// <summary>
    /// The rendered tile for a Temperature sensor differs between Units.MonitoringTempUnit
    /// "c" and "f" for the same reading, proving BuildMonitoringTileInput actually
    /// threads the preference into DeckMonitoringFormat.ResolveValueText's text
    /// (and so into the rendered pixels), not just leaving the service-formatted
    /// Celsius string on the physical key regardless of the app preference.
    /// </summary>
    [Fact]
    public void Tick_MonitoringSlot_TemperatureSensor_RendersDifferentTextForCelsiusVsFahrenheit()
    {
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" };

        var celsiusFixtures = NewFixtures(devicePresent: false);
        celsiusFixtures.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Temperature", Value = 65.3f, Units = "°C", Formatted = "65.3 °C", Parent = new SensorParent() },
        };
        var celsiusSurface = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        celsiusFixtures.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        // Both workers stay alive (using var, not a nested block) until the
        // bytes are captured: worker Dispose() resets the surface, which
        // clears its key images, so reading PeekKeyImage after disposal would
        // see a blanked tile instead of the rendered one.
        using var celsiusWorker = NewWorker(celsiusFixtures, celsiusSurface);
        celsiusWorker.Tick();
        var celsiusBytes = celsiusSurface.PeekKeyImage(0);

        var fahrenheitFixtures = NewFixtures(devicePresent: false);
        fahrenheitFixtures.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Temperature", Value = 65.3f, Units = "°C", Formatted = "65.3 °C", Parent = new SensorParent() },
        };
        var fahrenheitSurface = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        fahrenheitFixtures.Store.Update(s =>
        {
            s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
            {
                Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
            };
            s.Units.MonitoringTempUnit = "f";
        });
        using var fahrenheitWorker = NewWorker(fahrenheitFixtures, fahrenheitSurface);
        fahrenheitWorker.Tick();
        var fahrenheitBytes = fahrenheitSurface.PeekKeyImage(0);

        Assert.NotNull(celsiusBytes);
        Assert.NotNull(fahrenheitBytes);
        Assert.False(ImageEquals(celsiusBytes, fahrenheitBytes));
    }

    /// <summary>
    /// A monitoring key that already settled on a hash-skip (no push while
    /// the reading is unchanged) must still repaint the very next tick after
    /// Units.MonitoringTempUnit changes: the rendered ValueText changes even
    /// though the underlying sensor reading did not, so the wire-bytes hash
    /// changes too and the push is not masked by the unchanged-reading skip.
    /// </summary>
    [Fact]
    public void Tick_MonitoringSlot_TempUnitPreferenceChangesMidSession_RepaintsWithoutAValueChange()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Temperature", Value = 65.3f, Units = "°C", Formatted = "65.3 °C", Parent = new SensorParent() },
        };
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();
        worker.Tick();
        worker.Tick();
        Assert.Equal(2, simulated.SetKeyImageCallCount);

        f.Store.Update(s => s.Units.MonitoringTempUnit = "f");
        worker.Tick();

        Assert.True(simulated.SetKeyImageCallCount > 2);
    }

    [Fact]
    public void Tick_MonitoringSlot_UnresolvedSensor_RendersAPlaceholderInsteadOfLeavingTheKeyBlank()
    {
        var f = NewFixtures(devicePresent: false); // no CpuSensors configured: "cpu/missing" never resolves
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/missing", Style = "number" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();

        var bytes = simulated.PeekKeyImage(0);
        Assert.NotNull(bytes);
        Assert.True(Mini.IsValidWireImageLength(bytes!.Length));
    }

    [Fact]
    public void Tick_MonitoringSlot_UnresolvedSensor_PlaceholderIsHashSkippedOnRepeatTicks()
    {
        var f = NewFixtures(devicePresent: false);
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/missing", Style = "number" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);

        // Connect drives PushCurrentView's two-pass repaint: pass 1's empty
        // placeholder and pass 2's unresolved-sensor render happen to be
        // pixel-identical here, but pass 1 never hash-checks, so both push.
        worker.Tick();
        Assert.Equal(2, simulated.SetKeyImageCallCount);

        worker.Tick();
        worker.Tick();

        Assert.Equal(2, simulated.SetKeyImageCallCount);
    }

    [Fact]
    public void Tick_MonitoringSlot_PassesLabelTextAndFixedScaleThroughToTheRenderedTile()
    {
        HardwareSensor[] Sensors() => new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Clock", Value = 4500f, Formatted = "4500MHz", Parent = new SensorParent() },
        };

        var legacy = NewFixtures(devicePresent: false);
        legacy.Sensors.CpuSensors = Sensors();
        var legacyAction = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "line" };
        var legacySurface = new SimulatedStreamDeckSurface(Mini, "sim-legacy");
        legacy.Store.Update(s => s.StreamDeck.Decks["sim-legacy"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = legacyAction } } } } },
        });
        using var legacyWorker = NewWorker(legacy, legacySurface);
        legacyWorker.Tick();
        var legacyBytes = legacySurface.PeekKeyImage(0);

        var v3 = NewFixtures(devicePresent: false);
        v3.Sensors.CpuSensors = Sensors();
        var v3Action = new DeckAction
        {
            Type = "monitoring",
            Category = "cpu",
            Sensor = "cpu/core0",
            Style = "line",
            LabelText = "Custom Label",
            Scale = "fixed",
            Min = 0,
            Max = 10000,
        };
        var v3Surface = new SimulatedStreamDeckSurface(Mini, "sim-v3");
        v3.Store.Update(s => s.StreamDeck.Decks["sim-v3"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = v3Action } } } } },
        });
        using var v3Worker = NewWorker(v3, v3Surface);
        v3Worker.Tick();
        var v3Bytes = v3Surface.PeekKeyImage(0);

        Assert.NotNull(legacyBytes);
        Assert.NotNull(v3Bytes);
        Assert.NotEqual(legacyBytes, v3Bytes);
    }

    [Fact]
    public void PushCurrentView_NeverClearsALiveMonitoringKey()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "radial" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();
        Assert.NotNull(simulated.PeekKeyImage(0));

        // RefreshView drives the same PushCurrentView a nav/config change does;
        // it must not blank the monitoring key.
        worker.RefreshView("sim-0001");

        Assert.NotNull(simulated.PeekKeyImage(0));
    }

    [Fact]
    public void Tick_MonitoringSlot_RepaintsAfterNavigatingAwayAndBackWithAnUnchangedReading()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "line" } },
                            new DeckSlot { Action = new DeckAction { Type = "page", Op = "next" } },
                        },
                    },
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://page1.example.com" } },
                            new DeckSlot { Action = new DeckAction { Type = "page", Op = "prev" } },
                        },
                    },
                },
            },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();
        Assert.NotNull(simulated.PeekKeyImage(0));

        // Page 1's key 0 has no ImageRef, so navigating there clears the
        // physical key the monitoring slot used to own.
        simulated.Poke(1, true);
        worker.Tick();
        simulated.Poke(1, false);
        worker.Tick();
        Assert.Null(simulated.PeekKeyImage(0));

        // Navigate back with the sensor reading unchanged from the first
        // push. A quantized-unchanged reading must not suppress the repaint
        // just because the key was blanked while on page 1 in between.
        simulated.Poke(1, true);
        worker.Tick();
        simulated.Poke(1, false);
        worker.Tick();
        worker.Tick();

        Assert.NotNull(simulated.PeekKeyImage(0));
    }

    /// <summary>
    /// PushCurrentView paints every visible monitoring key immediately on
    /// connect (an uncapped nav-style repaint - see the PushCurrentView
    /// monitoring-slot branch), so the round-robin cap only bites once the
    /// view is already established and a later value change needs every key
    /// re-rendered. "number" style renders from ValueText alone, so its wire
    /// bytes stay stable across ticks unless the sensor value itself changes.
    /// </summary>
    [Fact]
    public void Tick_MoreMonitoringKeysThanTheCap_RoundRobinsAcrossTicks()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 1f, Formatted = "1%", Parent = new SensorParent() },
        };
        // One more monitoring slot than the per-tick push cap.
        var slots = new List<DeckSlot>();
        for (var i = 0; i < Mini.KeyCount - 1; i++)
        {
            slots.Add(new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } });
        }
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = slots } } },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();
        var baseline = SnapshotKeyImages(simulated, slots.Count);
        for (var i = 0; i < slots.Count; i++)
        {
            Assert.NotNull(baseline[i]);
        }

        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 99f, Formatted = "99%", Parent = new SensorParent() },
        };
        worker.Tick();
        var afterOneTick = SnapshotKeyImages(simulated, slots.Count);
        var updated = 0;
        for (var i = 0; i < slots.Count; i++)
        {
            if (!ImageEquals(baseline[i], afterOneTick[i]))
            {
                updated++;
            }
        }
        Assert.Equal(MonitoringPushCapPerTickForTests, updated);

        worker.Tick();
        var afterTwoTicks = SnapshotKeyImages(simulated, slots.Count);
        for (var i = 0; i < slots.Count; i++)
        {
            Assert.False(ImageEquals(baseline[i], afterTwoTicks[i]));
        }
    }

    /// <summary>Mirrors StreamDeckConnectionWorker's private MonitoringPushCapPerTick, which the test project cannot see directly.</summary>
    private const int MonitoringPushCapPerTickForTests = 4;

    /// <summary>
    /// While a monitoring key is physically held, RefreshMonitoringKeys must
    /// skip pushing it (avoiding a race with the pressed overlay) even though
    /// sampling still runs every tick - the only push during the hold is
    /// HandlePressVisual's own pressed inset, rendered from the key's last
    /// tile (still the pre-hold 10% reading, not the changed 90% one).
    /// Release forces a fresh push regardless of the hash-skip optimization,
    /// so the tile catches up immediately.
    /// </summary>
    [Fact]
    public void MonitoringSlot_HeldDuringATick_OnlyThePressedInsetPushes_ReleaseForcesAFreshOne()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 10f, Formatted = "10%", Parent = new SensorParent() },
        };
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();
        var initial = simulated.PeekKeyImage(0);
        Assert.NotNull(initial);
        var callsBeforeHold = simulated.SetKeyImageCallCount;

        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 90f, Formatted = "90%", Parent = new SensorParent() },
        };
        simulated.Poke(0, true);
        worker.Tick();

        var pressed = simulated.PeekKeyImage(0);
        Assert.NotEqual(initial, pressed);
        Assert.Equal(callsBeforeHold + 1, simulated.SetKeyImageCallCount);

        simulated.Poke(0, false);
        worker.Tick();

        Assert.NotEqual(initial, simulated.PeekKeyImage(0));
        Assert.NotEqual(pressed, simulated.PeekKeyImage(0));
        Assert.True(simulated.SetKeyImageCallCount > callsBeforeHold + 1);
    }

    /// <summary>
    /// Regression for the reported bug: pressing a page-nav key that reveals
    /// a monitoring slot must not leave the previous page's tile up. A single
    /// Tick() both processes the physical press (PumpSimulatedInput mirrors
    /// a real HID input report) and, via that press's own PushCurrentView
    /// call, repaints the newly visible key before this tick's monitoring
    /// pass ever runs.
    /// </summary>
    [Fact]
    public void Tick_NavigatingToAMonitoringSlot_PaintsItInTheSamePushCurrentViewPass()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number", LabelText = "PageZero" } },
                            new DeckSlot { Action = new DeckAction { Type = "page", Op = "next" } },
                        },
                    },
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number", LabelText = "PageOne" } },
                            new DeckSlot { Action = new DeckAction { Type = "page", Op = "prev" } },
                        },
                    },
                },
            },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();
        var pageZeroImage = simulated.PeekKeyImage(0);
        Assert.NotNull(pageZeroImage);

        simulated.Poke(1, true);
        worker.Tick();

        var pageOneImage = simulated.PeekKeyImage(0);
        Assert.NotNull(pageOneImage);
        Assert.NotEqual(pageZeroImage, pageOneImage);
    }

    /// <summary>Same nav path, but the landing key's sensor id never resolves - it must still get a placeholder, never the departing page's pixels.</summary>
    [Fact]
    public void Tick_NavigatingToAnUnresolvedMonitoringSlot_PushesAPlaceholderNotStalePixels()
    {
        var f = NewFixtures(devicePresent: false); // no CpuSensors configured: "cpu/missing" never resolves
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Label = "PageZero", Action = new DeckAction { Type = "openUrl", Url = "https://page0.example.com" } },
                            new DeckSlot { Action = new DeckAction { Type = "page", Op = "next" } },
                        },
                    },
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/missing", Style = "number" } },
                            new DeckSlot { Action = new DeckAction { Type = "page", Op = "prev" } },
                        },
                    },
                },
            },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();
        Assert.Null(simulated.PeekKeyImage(0)); // page 0's slot has no ImageRef

        simulated.Poke(1, true);
        worker.Tick();

        var bytes = simulated.PeekKeyImage(0);
        Assert.NotNull(bytes);
        Assert.True(Mini.IsValidWireImageLength(bytes!.Length));
    }

    /// <summary>
    /// Regression for the reported bug: on a view with more than one
    /// monitoring key, PushCurrentView must push every key's empty
    /// placeholder (pass 1) before any key's real content (pass 2) - so the
    /// whole view goes clean instantly on nav, instead of an earlier key's
    /// expensive sample+render delaying a later key's placeholder while it
    /// still shows the previous view's pixels.
    /// </summary>
    [Fact]
    public void PushCurrentView_MultipleMonitoringKeys_PushesEveryPlaceholderBeforeAnyRealContent()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 10f, Formatted = "10%", Parent = new SensorParent() },
        };
        var slots = new List<DeckSlot>
        {
            new() { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } },
            new() { Action = new DeckAction { Type = "openUrl", Url = "https://example.com" } },
            new() { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = slots } } },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick(); // connect drives one PushCurrentView call

        var monitoringPushes = new List<int>();
        foreach (var key in simulated.SetKeyImageOrder)
        {
            if (key == 0 || key == 2)
            {
                monitoringPushes.Add(key);
            }
        }
        // Both keys' placeholders (pass 1) precede both keys' real content
        // (pass 2) - never key 0's real content ahead of key 2's placeholder.
        Assert.Equal(new[] { 0, 2, 0, 2 }, monitoringPushes);
    }

    /// <summary>
    /// The nav's own PushCurrentView pushes twice for the monitoring key (an
    /// empty placeholder, then the real tile - see PushCurrentView's two-pass
    /// repaint), and that same tick's own RefreshMonitoringKeys pass must not
    /// push a third time for the nav, nor push again on the next tick while
    /// the reading is unchanged.
    /// </summary>
    [Fact]
    public void Tick_NavigatingToAMonitoringSlot_TheFollowingTickDoesNotRepushAnUnchangedTile()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 42f, Formatted = "42%", Parent = new SensorParent() },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://page0.example.com" } },
                            new DeckSlot { Action = new DeckAction { Type = "page", Op = "next" } },
                        },
                    },
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } },
                            new DeckSlot { Action = new DeckAction { Type = "page", Op = "prev" } },
                        },
                    },
                },
            },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();
        var callsBeforeNav = simulated.SetKeyImageCallCount;

        simulated.Poke(1, true);
        worker.Tick();
        simulated.Poke(1, false);
        worker.Tick();
        var callsAfterNav = simulated.SetKeyImageCallCount;
        Assert.Equal(callsBeforeNav + 2, callsAfterNav);

        worker.Tick();
        Assert.Equal(callsAfterNav, simulated.SetKeyImageCallCount);
    }

    [Fact]
    public void SampleMonitoringHistory_SensorGoesUnresolvedThenReturns_DoesNotAppendZeroTrough()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 10f, Formatted = "10%", Parent = new SensorParent() },
        };
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "line" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();
        worker.Tick();
        worker.Tick();

        var beforeUnresolved = worker.MonitoringHistoryForTests("sim-0001", 0, "0");
        Assert.NotNull(beforeUnresolved);
        Assert.All(beforeUnresolved!, v => Assert.Equal(10f, v));
        var sampleCount = beforeUnresolved!.Count;

        // The sensor id disappears (device unplugged, id gone). More ticks
        // than MonitoringHistoryLength so a zero-trough bug would clearly
        // show as both growth and altered values.
        f.Sensors.CpuSensors = Array.Empty<HardwareSensor>();
        for (var i = 0; i < 45; i++)
        {
            worker.Tick();
        }

        var whileUnresolved = worker.MonitoringHistoryForTests("sim-0001", 0, "0");
        Assert.NotNull(whileUnresolved);
        Assert.Equal(sampleCount, whileUnresolved!.Count);
        Assert.All(whileUnresolved!, v => Assert.Equal(10f, v));

        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 55f, Formatted = "55%", Parent = new SensorParent() },
        };
        worker.Tick();

        var afterResolved = worker.MonitoringHistoryForTests("sim-0001", 0, "0");
        Assert.NotNull(afterResolved);
        Assert.Equal(sampleCount + 1, afterResolved!.Count);
        Assert.Equal(55f, afterResolved![^1]);
    }

    [Fact]
    public void SampleMonitoringHistory_NeverResolved_HistoryStaysEmpty()
    {
        var f = NewFixtures(devicePresent: false); // no CpuSensors configured: "cpu/missing" never resolves
        var action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/missing", Style = "number" };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } },
        });
        using var worker = NewWorker(f, simulated);

        for (var i = 0; i < 5; i++)
        {
            worker.Tick();
        }

        Assert.Null(worker.MonitoringHistoryForTests("sim-0001", 0, "0"));
    }

    /// <summary>
    /// The deck-config PUT route saves the new config then calls RefreshView
    /// (PushCurrentView) while the deck stays connected - the eviction path
    /// under test must run there, not only on disconnect.
    /// </summary>
    [Fact]
    public void PushCurrentView_ConfigEditRetypesAMonitoringSlot_EvictsItsHistoryAndHash_SurvivingSlotHistoryUntouched()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 7f, Formatted = "7%", Parent = new SensorParent() },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } },
                            new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } },
                        },
                    },
                },
            },
        });
        using var worker = NewWorker(f, simulated);

        worker.Tick();
        worker.Tick();
        worker.Tick();

        var survivorHistoryBefore = worker.MonitoringHistoryForTests("sim-0001", 0, "0");
        Assert.NotNull(survivorHistoryBefore);
        Assert.NotNull(worker.MonitoringHistoryForTests("sim-0001", 0, "1"));
        Assert.True(worker.HasMonitoringHashForTests("sim-0001", 0, "1"));

        // Config edit retypes slot 1 away from monitoring, as the deck-config
        // PUT route's persisted change would.
        f.Store.Update(s =>
        {
            s.StreamDeck.Decks["sim-0001"].Deck.Pages[0].Slots[1] =
                new DeckSlot { Action = new DeckAction { Type = "openUrl", Url = "https://example.com" } };
        });
        worker.RefreshView("sim-0001");

        Assert.Null(worker.MonitoringHistoryForTests("sim-0001", 0, "1"));
        Assert.False(worker.HasMonitoringHashForTests("sim-0001", 0, "1"));

        // RefreshView's own PushCurrentView call inline-samples every still-
        // visible monitoring slot (see the PushCurrentView monitoring-slot
        // branch), so slot 0 gains exactly one more sample - it must not be
        // reset to a fresh single-sample buffer by the eviction pass.
        var survivorHistoryAfter = worker.MonitoringHistoryForTests("sim-0001", 0, "0");
        Assert.NotNull(survivorHistoryAfter);
        Assert.Equal(survivorHistoryBefore!.Count + 1, survivorHistoryAfter!.Count);
        for (var i = 0; i < survivorHistoryBefore.Count; i++)
        {
            Assert.Equal(survivorHistoryBefore[i], survivorHistoryAfter[i]);
        }
    }

    /// <summary>Deleting a page orphans that page's monitoring entries the same way retyping a slot does; ResolveSlot returns null once the page index is out of range.</summary>
    [Fact]
    public void PushCurrentView_ConfigEditDeletesAPage_EvictsThatPagesMonitoringHistory()
    {
        var f = NewFixtures(devicePresent: false);
        f.Sensors.CpuSensors = new[]
        {
            new HardwareSensor { Id = "cpu/core0", Name = "Core 0", Type = "Load", Value = 3f, Formatted = "3%", Parent = new SensorParent() },
        };
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        f.Store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "page", Op = "next" } } } },
                    new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "monitoring", Category = "cpu", Sensor = "cpu/core0", Style = "number" } } } },
                },
            },
        });
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        Assert.True(worker.SetNav("sim-0001", 1, Array.Empty<int>()));
        worker.Tick();
        worker.Tick();
        Assert.NotNull(worker.MonitoringHistoryForTests("sim-0001", 1, "0"));

        // Config edit deletes page 1; the deck's tracked current page (still
        // 1 at this point) will clamp back to 0 once PushCurrentView runs.
        f.Store.Update(s =>
        {
            s.StreamDeck.Decks["sim-0001"].Deck.Pages.RemoveAt(1);
        });
        worker.RefreshView("sim-0001");

        Assert.Null(worker.MonitoringHistoryForTests("sim-0001", 1, "0"));
    }

    /// <summary>
    /// DisconnectAll (driven here via the feature gate turning off, the same
    /// path service shutdown takes) restores the deck's persisted brightness
    /// and fires a firmware Reset() on every real surface before tearing it
    /// down, so it shows the built-in Elgato boot logo instead of freezing
    /// on its last live frame.
    /// </summary>
    [Fact]
    public void DisconnectAll_RealSurface_RestoresBrightnessAndResetsBeforeTeardown()
    {
        var f = NewFixtures(devicePresent: true);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        f.Store.Update(s => s.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings { Brightness = 55 });
        using var worker = NewWorker(f);
        worker.Tick();
        var dev = (MockStreamDeckHidDevice)f.Hid.DevicesByPath["path-1"];
        dev.FeatureWrites.Clear(); // drop the connect-time brightness write

        f.Gate.SetEnabled("streamdeck", false);
        worker.Tick();

        Assert.Contains(StreamDeckProtocol.BuildBrightnessFeature(55), dev.FeatureWrites);
        Assert.Contains(StreamDeckProtocol.BuildResetFeature(), dev.FeatureWrites);
        Assert.True(dev.Disposed);
    }

    /// <summary>
    /// FastServiceShutdown's real-quit path (SCM stop, /service/stop, tray
    /// shut down) resolves the worker and calls this directly - it must
    /// reset every connected real surface, skip the simulated one, and
    /// leave tracked state intact (unlike DisconnectAll, nothing tears the
    /// surface down since the process is exiting anyway).
    /// </summary>
    [Fact]
    public void ResetConnectedSurfacesForShutdown_ResetsRealSurfaces_SkipsSimulated_LeavesStateTracked()
    {
        var f = NewFixtures(devicePresent: true);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        f.Store.Update(s => s.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings { Brightness = 55 });
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        using var worker = NewWorker(f, simulated);
        worker.Tick();
        var dev = (MockStreamDeckHidDevice)f.Hid.DevicesByPath["path-1"];
        dev.FeatureWrites.Clear(); // drop the connect-time brightness write

        worker.ResetConnectedSurfacesForShutdown();

        Assert.Contains(StreamDeckProtocol.BuildBrightnessFeature(55), dev.FeatureWrites);
        Assert.Contains(StreamDeckProtocol.BuildResetFeature(), dev.FeatureWrites);
        Assert.False(dev.Disposed);
        Assert.Equal(0, simulated.ResetCount);
        Assert.True(simulated.IsConnected);
        Assert.Equal(2, worker.Surfaces.Count);
    }

    /// <summary>The simulated deck is a shared DI singleton other routes hold a reference to; DisconnectAll must never reset or dispose it.</summary>
    [Fact]
    public void DisconnectAll_SkipsTheSimulatedSurface_NeverResetsIt()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        using var worker = NewWorker(f, simulated);
        worker.Tick();

        f.Gate.SetEnabled("streamdeck", false);
        worker.Tick();

        Assert.True(simulated.IsConnected);
        Assert.Equal(0, simulated.ResetCount);
    }

    /// <summary>A second DisconnectAll pass (e.g. the gate staying off across ticks) finds an already-empty surface set and does nothing further.</summary>
    [Fact]
    public void DisconnectAll_CalledAgainWithNothingConnected_IsANoOp()
    {
        var f = NewFixtures(devicePresent: true);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        using var worker = NewWorker(f);
        worker.Tick();
        var dev = (MockStreamDeckHidDevice)f.Hid.DevicesByPath["path-1"];

        f.Gate.SetEnabled("streamdeck", false);
        worker.Tick();
        var writesAfterFirstDisconnect = dev.FeatureWrites.Count;
        Assert.True(dev.Disposed);
        Assert.Empty(worker.Surfaces);

        worker.Tick();

        Assert.Equal(writesAfterFirstDisconnect, dev.FeatureWrites.Count);
        Assert.Empty(worker.Surfaces);
    }

    /// <summary>An unplug (or a dead read handle) removes the surface without ever resetting it - a vanished device cannot be written to.</summary>
    [Fact]
    public void Tick_DeviceUnplugged_DoesNotResetTheSurface()
    {
        var f = NewFixtures(devicePresent: false);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        var usb = new MutableUsbEnumerator();
        usb.Devices.Add(new UsbDeviceEntry { VendorId = StreamDeckModels.VendorId, ProductId = Mini.ProductId });
        var presence = new HardwarePresence(usb);
        using var worker = new StreamDeckConnectionWorker(f.Hid, presence, f.Gate, f.Store, f.Executor, f.ImageCache, f.Hub, f.Sensors);

        worker.Tick();
        var dev = (MockStreamDeckHidDevice)f.Hid.DevicesByPath["path-1"];
        dev.FeatureWrites.Clear();

        usb.Devices.Clear();
        f.Hid.ByProductId.Clear();
        worker.Tick();

        Assert.DoesNotContain(StreamDeckProtocol.BuildResetFeature(), dev.FeatureWrites);
        Assert.True(dev.Disposed);
    }

    private sealed class MutableUsbEnumerator : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Devices { get; } = new();
        public List<UsbDeviceEntry> Enumerate() => Devices;
    }
}
