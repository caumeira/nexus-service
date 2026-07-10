using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Deck;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
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
        MultiplexHub Hub);

    private static Fixtures NewFixtures(bool devicePresent)
    {
        var hid = new FakeWorkerHidEnumerator();
        var usbEntries = devicePresent
            ? new List<UsbDeviceEntry> { new() { VendorId = StreamDeckModels.VendorId, ProductId = Mini.ProductId } }
            : new List<UsbDeviceEntry>();
        var presence = new HardwarePresence(new FixedUsbEnumerator(usbEntries.ToArray()));
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        var imageCache = new StreamDeckImageCache(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexus-streamdeck-test-" + Guid.NewGuid().ToString("N")));
        return new Fixtures(hid, presence, gate, store, new FakeDeckActionExecutor(), imageCache, new MultiplexHub());
    }

    private static StreamDeckConnectionWorker NewWorker(Fixtures f, SimulatedStreamDeckSurface? simulated = null) =>
        new(f.Hid, f.Presence, f.Gate, f.Store, f.Executor, f.ImageCache, f.Hub, simulated);

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
        var worker = NewWorker(f);

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
        var worker = NewWorker(f);

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
        var worker = NewWorker(f);

        worker.Tick();

        Assert.Empty(worker.Surfaces);
    }

    [Fact]
    public void Tick_NoUsbPresence_SkipsHidScanEntirely()
    {
        var f = NewFixtures(devicePresent: false);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1"); // hid would find it, but presence says no 0x0FD9 on the bus
        var worker = NewWorker(f);

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
        var worker = new StreamDeckConnectionWorker(f.Hid, presence, f.Gate, f.Store, f.Executor, f.ImageCache, f.Hub);

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
        var worker = new StreamDeckConnectionWorker(f.Hid, presence, f.Gate, f.Store, f.Executor, f.ImageCache, f.Hub);

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
        var worker = NewWorker(f, simulated);

        worker.Tick();

        Assert.NotNull(worker.FindBySerial("sim-0001"));
    }

    [Fact]
    public void Tick_WithSimulatedSurface_PumpsPressesAcrossMultipleTicksWithoutError()
    {
        var f = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var worker = NewWorker(f, simulated);
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
        var worker = NewWorker(f, simulated);
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
            Deck = new DeckConfig { Slots = { new DeckSlot { Action = action } } },
        });
        var worker = NewWorker(f, simulated);
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
    public async Task Tick_DrainsMultipleQueuedReportsInASingleTick()
    {
        var f = NewFixtures(devicePresent: true);
        AddMiniDevice(f.Hid, "path-1", "SERIAL-1");
        var actionA = new DeckAction { Type = "openUrl", Url = "https://a.example.com" };
        var actionB = new DeckAction { Type = "openUrl", Url = "https://b.example.com" };
        f.Store.Update(s => s.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings
        {
            Deck = new DeckConfig { Slots = { new DeckSlot { Action = actionA }, new DeckSlot { Action = actionB } } },
        });
        var worker = NewWorker(f);
        worker.Tick();

        // Two full press+release cycles queued as 4 separate HID reports, all
        // already sitting in the device's read queue before the next tick -
        // simulates two rapid presses landing inside one 1-second tick window.
        var device = (MockStreamDeckHidDevice)f.Hid.DevicesByPath["path-1"];
        device.PendingReads.Enqueue(new byte[] { 0x01, 1, 0, 0, 0, 0, 0 }); // key 0 down
        device.PendingReads.Enqueue(new byte[] { 0x01, 0, 0, 0, 0, 0, 0 }); // key 0 up
        device.PendingReads.Enqueue(new byte[] { 0x01, 0, 1, 0, 0, 0, 0 }); // key 1 down
        device.PendingReads.Enqueue(new byte[] { 0x01, 0, 0, 0, 0, 0, 0 }); // key 1 up

        worker.Tick();
        for (var i = 0; i < 50 && f.Executor.Calls.Count < 2; i++)
        {
            await Task.Delay(10);
        }

        // Both dispatches are fire-and-forget Task.Run work items (HandleKeyDown
        // never awaits them), so their thread-pool execution order relative to
        // each other isn't guaranteed - assert the set, not indexed positions.
        Assert.Equal(2, f.Executor.Calls.Count);
        Assert.Contains(f.Executor.Calls, c => c.KeyIndex == 0 && ReferenceEquals(c.Action, actionA));
        Assert.Contains(f.Executor.Calls, c => c.KeyIndex == 1 && ReferenceEquals(c.Action, actionB));
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
                Slots =
                {
                    new DeckSlot { Folder = new DeckFolder { Slots = { new DeckSlot { Action = innerAction } } } },
                },
            },
        });
        var worker = NewWorker(f, simulated);
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
                Slots = { new DeckSlot { Folder = new DeckFolder { Slots = { new DeckSlot() } } } },
            },
        });
        var worker = NewWorker(f, simulated);
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
                Slots = { new DeckSlot { Folder = new DeckFolder { Slots = { new DeckSlot() } } } },
            },
        });
        var worker = NewWorker(f, simulated);
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

    private sealed class MutableUsbEnumerator : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Devices { get; } = new();
        public List<UsbDeviceEntry> Enumerate() => Devices;
    }
}
