using System.Collections.Generic;
using System.Linq;
using Qos.Service.Devices;
using Qos.Service.Devices.Detection;
using Qos.Service.Devices.Handlers;
using Qos.Service.Models.Devices;
using Xunit;

namespace Qos.Service.Tests;

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
        new CnvsHandler(),
        new Q60Handler(),
        new FanHubHandler(),
    };

    // ---- DeviceManager.GetAll() returns all registered handlers as DeviceListItems ----

    [Fact]
    public void GetAll_ReturnsOneItemPerHandler()
    {
        var manager = new DeviceManager(AllHandlers, new StubUsbEnumerator());

        var items = manager.GetAll();

        Assert.Equal(AllHandlers.Length, items.Count);
        Assert.Contains(items, i => i.Id == "cnvs");
        Assert.Contains(items, i => i.Id == "q60");
        Assert.Contains(items, i => i.Id == "fan-hub");
    }

    [Fact]
    public void GetAll_PopulatesNameAndCategory()
    {
        var manager = new DeviceManager(AllHandlers, new StubUsbEnumerator());

        var items = manager.GetAll();

        var cnvs = items.Single(i => i.Id == "cnvs");
        Assert.Equal("CNVS", cnvs.Name);
        Assert.Equal("controller", cnvs.Category);

        var q60 = items.Single(i => i.Id == "q60");
        Assert.Equal("Q60", q60.Name);
        Assert.Equal("display", q60.Category);
    }

    // ---- Handler.IsConnected() — returns true when matching VID/PID is in the device list ----

    [Fact]
    public void IsConnected_ReturnsTrue_WhenMatchingDevicePresent()
    {
        var handler = new Q60Handler();
        var devices = new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x3402, ProductId = 0x0600, Name = "Q60" },
        };

        Assert.True(handler.IsConnected(devices));
    }

    // ---- Handler.IsConnected() — returns false when no match ----

    [Fact]
    public void IsConnected_ReturnsFalse_WhenNoMatchingDevice()
    {
        var handler = new Q60Handler();
        var devices = new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x1234, ProductId = 0x5678, Name = "Unrelated Device" },
        };

        Assert.False(handler.IsConnected(devices));
    }

    [Fact]
    public void IsConnected_ReturnsFalse_WhenListIsEmpty()
    {
        var handler = new Q60Handler();

        Assert.False(handler.IsConnected(new List<UsbDeviceEntry>()));
    }

    // ---- CnvsHandler recognizes multiple CNVS variants ----

    [Theory]
    [InlineData(0x0BFF)]
    [InlineData(0x0B00)]
    [InlineData(0x0B01)]
    [InlineData(0x0B02)]
    public void CnvsHandler_RecognizesAllVariants(int productId)
    {
        var handler = new CnvsHandler();
        var devices = new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x3402, ProductId = productId, Name = "CNVS Variant" },
        };

        Assert.True(handler.IsConnected(devices));
    }

    [Fact]
    public void CnvsHandler_DoesNotMatchUnknownPid()
    {
        var handler = new CnvsHandler();
        var devices = new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x3402, ProductId = 0x9999, Name = "Unknown" },
        };

        Assert.False(handler.IsConnected(devices));
    }

    // ---- DeviceManager with empty USB list shows all devices as disconnected ----

    [Fact]
    public void GetAll_WithEmptyUsbList_AllDevicesDisconnected()
    {
        var manager = new DeviceManager(AllHandlers, new StubUsbEnumerator());

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
        var manager = new DeviceManager(AllHandlers, enumerator);

        var items = manager.GetAll();

        Assert.True(items.Single(i => i.Id == "q60").Connected);
        Assert.False(items.Single(i => i.Id == "cnvs").Connected);
        Assert.False(items.Single(i => i.Id == "fan-hub").Connected);
    }

    // ---- Handler modularity — DeviceManager works with any subset of handlers ----

    [Fact]
    public void GetAll_WithSingleHandler_ReturnsOnlyThatDevice()
    {
        var manager = new DeviceManager(
            new IDeviceHandler[] { new Q60Handler() },
            new StubUsbEnumerator()
        );

        var items = manager.GetAll();

        Assert.Single(items);
        Assert.Equal("q60", items[0].Id);
    }

    [Fact]
    public void GetAll_WithNoHandlers_ReturnsEmptyList()
    {
        var manager = new DeviceManager(
            Enumerable.Empty<IDeviceHandler>(),
            new StubUsbEnumerator()
        );

        var items = manager.GetAll();

        Assert.Empty(items);
    }

    [Fact]
    public void GetAll_WithSubsetOfHandlers_ReturnsCorrectCount()
    {
        var manager = new DeviceManager(
            new IDeviceHandler[] { new CnvsHandler(), new FanHubHandler() },
            new StubUsbEnumerator()
        );

        var items = manager.GetAll();

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Id == "cnvs");
        Assert.Contains(items, i => i.Id == "fan-hub");
        Assert.DoesNotContain(items, i => i.Id == "q60");
    }
}
