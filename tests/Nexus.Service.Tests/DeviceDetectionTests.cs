using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Models.Devices;
using Nexus.Service.Plugins;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>Test USB enumerator that returns a fixed list of devices.</summary>
internal sealed class FixedUsbEnumerator : IUsbEnumerator
{
    private readonly List<UsbDeviceEntry> _devices;

    public FixedUsbEnumerator(params UsbDeviceEntry[] devices)
    {
        _devices = devices.ToList();
    }

    public List<UsbDeviceEntry> Enumerate() => _devices;
}

public class DeviceDetectionTests
{
    private static readonly IDeviceHandler[] AllHandlers =
    {
        TestHandlers.Cnvs(),
        TestHandlers.QSeries(),
        TestHandlers.FanHub(),
    };

    private static DeviceControlGate NewGate() => new(new InMemoryConfigStore());

    // ---- DeviceManager with empty USB list shows all devices as disconnected ----

    [Fact]
    public void GetAll_WithEmptyUsbList_AllDevicesDisconnected()
    {
        var manager = new DeviceManager(AllHandlers, new StubUsbEnumerator(), new PluginProviderRegistry(), NewGate());

        var items = manager.GetAll();

        Assert.All(items, item => Assert.False(item.Connected));
    }

    // ---- DeviceManager with detected devices marks them connected ----

    [Fact]
    public void GetAll_WithMatchingDevices_MarksConnected()
    {
        var enumerator = new FixedUsbEnumerator(
            new UsbDeviceEntry { VendorId = 0x3402, ProductId = 0x0600, Name = "Q60" }
        );
        var manager = new DeviceManager(AllHandlers, enumerator, new PluginProviderRegistry(), NewGate());

        var items = manager.GetAll();

        Assert.True(items.Single(i => i.Id == "qseries").Connected);
        Assert.False(items.Single(i => i.Id == "cnvs").Connected);
        Assert.False(items.Single(i => i.Id == "fan-hub").Connected);
    }

    // ---- Handler modularity - DeviceManager works with any subset of handlers ----

    // ---- Tryx recognized by its current 391A:1011 firmware, not legacy 18D1 ----

    [Fact]
    public void Tryx_IsConnected_ForCurrentRkFirmware()
    {
        var handler = new TryxHandler();
        var devices = new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x391A, ProductId = 0x1011, Name = "PANO" },
        };

        Assert.True(handler.IsConnected(devices));
    }

    [Fact]
    public void Tryx_IsConnected_ForPanorama360SePid()
    {
        var handler = new TryxHandler();
        var devices = new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x391A, ProductId = 0x1021, Name = "PASE" },
        };

        Assert.True(handler.IsConnected(devices));
    }

    [Fact]
    public void Tryx_IsConnected_ForPanorama360SeSecondPid()
    {
        var handler = new TryxHandler();
        var devices = new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x391A, ProductId = 0x1061, Name = "PASE" },
        };

        Assert.True(handler.IsConnected(devices));
    }

    [Fact]
    public void Tryx_NotConnected_ForRetiredLegacy18D1Firmware()
    {
        var handler = new TryxHandler();
        var devices = new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x18D1, ProductId = 0x2D03, Name = "cm01" },
        };

        Assert.False(handler.IsConnected(devices));
    }

    [Fact]
    public void GetAll_WithSingleHandler_ReturnsOnlyThatDevice()
    {
        var manager = new DeviceManager(
            new IDeviceHandler[] { TestHandlers.QSeries() },
            new StubUsbEnumerator(), new PluginProviderRegistry(), NewGate()
        );

        var items = manager.GetAll();

        Assert.Single(items);
        Assert.Equal("qseries", items[0].Id);
    }

    [Fact]
    public void GetAll_WithNoHandlers_ReturnsEmptyList()
    {
        var manager = new DeviceManager(
            Enumerable.Empty<IDeviceHandler>(),
            new StubUsbEnumerator(), new PluginProviderRegistry(), NewGate()
        );

        var items = manager.GetAll();

        Assert.Empty(items);
    }

    [Fact]
    public void GetAll_WithSubsetOfHandlers_ReturnsCorrectCount()
    {
        var manager = new DeviceManager(
            new IDeviceHandler[] { TestHandlers.Cnvs(), TestHandlers.FanHub() },
            new StubUsbEnumerator(), new PluginProviderRegistry(), NewGate()
        );

        var items = manager.GetAll();

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Id == "cnvs");
        Assert.Contains(items, i => i.Id == "fan-hub");
        Assert.DoesNotContain(items, i => i.Id == "qseries");
    }
}
