using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Persistence;

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

public class StreamDeckConnectionWorkerTests
{
    private static readonly StreamDeckModel Mini = StreamDeckModels.ByProductId(0x0063)!;

    private static (FakeWorkerHidEnumerator hid, HardwarePresence presence, DeviceControlGate gate) NewFixtures(bool devicePresent)
    {
        var hid = new FakeWorkerHidEnumerator();
        var usbEntries = devicePresent
            ? new List<UsbDeviceEntry> { new() { VendorId = StreamDeckModels.VendorId, ProductId = Mini.ProductId } }
            : new List<UsbDeviceEntry>();
        var presence = new HardwarePresence(new FixedUsbEnumerator(usbEntries.ToArray()));
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        return (hid, presence, gate);
    }

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
        var (hid, presence, gate) = NewFixtures(devicePresent: true);
        AddMiniDevice(hid, "path-1", "SERIAL-1");
        var worker = new StreamDeckConnectionWorker(hid, presence, gate);

        worker.Tick();

        Assert.Single(worker.Surfaces);
        Assert.NotNull(worker.FindBySerial("SERIAL-1"));
        Assert.True(worker.FindBySerial("SERIAL-1")!.IsConnected);
    }

    [Fact]
    public void Tick_GateDisabled_NeverConnects()
    {
        var (hid, presence, gate) = NewFixtures(devicePresent: true);
        AddMiniDevice(hid, "path-1", "SERIAL-1");
        gate.SetEnabled("streamdeck", false);
        var worker = new StreamDeckConnectionWorker(hid, presence, gate);

        worker.Tick();

        Assert.Empty(worker.Surfaces);
    }

    [Fact]
    public void Tick_NoUsbPresence_SkipsHidScanEntirely()
    {
        var (hid, presence, gate) = NewFixtures(devicePresent: false);
        AddMiniDevice(hid, "path-1", "SERIAL-1"); // hid would find it, but presence says no 0x0FD9 on the bus
        var worker = new StreamDeckConnectionWorker(hid, presence, gate);

        worker.Tick();

        Assert.Empty(worker.Surfaces);
    }

    [Fact]
    public void Tick_DeviceUnplugged_DisposesAndRemovesTheSurface()
    {
        var hid = new FakeWorkerHidEnumerator();
        AddMiniDevice(hid, "path-1", "SERIAL-1");
        var usb = new MutableUsbEnumerator();
        usb.Devices.Add(new UsbDeviceEntry { VendorId = StreamDeckModels.VendorId, ProductId = Mini.ProductId });
        var presence = new HardwarePresence(usb);
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        var worker = new StreamDeckConnectionWorker(hid, presence, gate);

        worker.Tick();
        var dev = (MockStreamDeckHidDevice)hid.DevicesByPath["path-1"];
        Assert.Single(worker.Surfaces);

        usb.Devices.Clear();
        hid.ByProductId.Clear();
        worker.Tick();

        Assert.Empty(worker.Surfaces);
        Assert.True(dev.Disposed);
    }

    [Fact]
    public void Tick_WithSimulatedSurface_RegistersItOnFirstTick()
    {
        var (hid, presence, gate) = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var worker = new StreamDeckConnectionWorker(hid, presence, gate, simulated);

        worker.Tick();

        Assert.NotNull(worker.FindBySerial("sim-0001"));
    }

    [Fact]
    public void Tick_WithSimulatedSurface_PumpsPressesAcrossMultipleTicksWithoutError()
    {
        var (hid, presence, gate) = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var worker = new StreamDeckConnectionWorker(hid, presence, gate, simulated);
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
        var (hid, presence, gate) = NewFixtures(devicePresent: false);
        var simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var worker = new StreamDeckConnectionWorker(hid, presence, gate, simulated);
        worker.Tick();

        gate.SetEnabled("streamdeck", false);
        worker.Tick();

        Assert.True(simulated.IsConnected);
    }

    private sealed class MutableUsbEnumerator : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Devices { get; } = new();
        public List<UsbDeviceEntry> Enumerate() => Devices;
    }
}
