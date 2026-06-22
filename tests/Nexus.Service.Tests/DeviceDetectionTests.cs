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

    // ---- DeviceManager with empty USB list shows all devices as disconnected ----

    [Fact]
    public void GetAll_WithEmptyUsbList_AllDevicesDisconnected()
    {
        var manager = new DeviceManager(AllHandlers, new StubUsbEnumerator(), new PluginProviderRegistry());

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
        var manager = new DeviceManager(AllHandlers, enumerator, new PluginProviderRegistry());

        var items = manager.GetAll();

        Assert.True(items.Single(i => i.Id == "qseries").Connected);
        Assert.False(items.Single(i => i.Id == "cnvs").Connected);
        Assert.False(items.Single(i => i.Id == "fan-hub").Connected);
    }

    // ---- Handler modularity - DeviceManager works with any subset of handlers ----

    [Fact]
    public void GetAll_WithSingleHandler_ReturnsOnlyThatDevice()
    {
        var manager = new DeviceManager(
            new IDeviceHandler[] { TestHandlers.QSeries() },
            new StubUsbEnumerator(), new PluginProviderRegistry()
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
            new StubUsbEnumerator(), new PluginProviderRegistry()
        );

        var items = manager.GetAll();

        Assert.Empty(items);
    }

    [Fact]
    public void GetAll_WithSubsetOfHandlers_ReturnsCorrectCount()
    {
        var manager = new DeviceManager(
            new IDeviceHandler[] { TestHandlers.Cnvs(), TestHandlers.FanHub() },
            new StubUsbEnumerator(), new PluginProviderRegistry()
        );

        var items = manager.GetAll();

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Id == "cnvs");
        Assert.Contains(items, i => i.Id == "fan-hub");
        Assert.DoesNotContain(items, i => i.Id == "qseries");
    }
}
