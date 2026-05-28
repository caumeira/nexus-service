using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Models.Devices;
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
        new QSeriesHandler(),
        TestHandlers.FanHub(),
    };

    // ---- DeviceManager.GetAll() returns all registered handlers as DeviceListItems ----

    [Fact]
    public void GetAll_ReturnsOneItemPerHandler()
    {
        var manager = new DeviceManager(AllHandlers, new StubUsbEnumerator());

        var items = manager.GetAll();

        Assert.Equal(AllHandlers.Length, items.Count);
        Assert.Contains(items, i => i.Id == "cnvs");
        Assert.Contains(items, i => i.Id == "qseries");
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

        var qs = items.Single(i => i.Id == "qseries");
        Assert.Equal("Q-series", qs.Name);
        Assert.Equal("display", qs.Category);
    }

    // ---- Handler.IsConnected() — returns true when matching VID/PID is in the device list ----

    [Fact]
    public void IsConnected_ReturnsTrue_WhenMatchingDevicePresent()
    {
        var handler = new QSeriesHandler();
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
        var handler = new QSeriesHandler();
        var devices = new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x1234, ProductId = 0x5678, Name = "Unrelated Device" },
        };

        Assert.False(handler.IsConnected(devices));
    }

    [Fact]
    public void IsConnected_ReturnsFalse_WhenListIsEmpty()
    {
        var handler = new QSeriesHandler();

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
        var handler = TestHandlers.Cnvs();
        var devices = new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x3402, ProductId = productId, Name = "CNVS Variant" },
        };

        Assert.True(handler.IsConnected(devices));
    }

    [Fact]
    public void CnvsHandler_DoesNotMatchUnknownPid()
    {
        var handler = TestHandlers.Cnvs();
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

        Assert.True(items.Single(i => i.Id == "qseries").Connected);
        Assert.False(items.Single(i => i.Id == "cnvs").Connected);
        Assert.False(items.Single(i => i.Id == "fan-hub").Connected);
    }

    // ---- Handler modularity — DeviceManager works with any subset of handlers ----

    [Fact]
    public void GetAll_WithSingleHandler_ReturnsOnlyThatDevice()
    {
        var manager = new DeviceManager(
            new IDeviceHandler[] { new QSeriesHandler() },
            new StubUsbEnumerator()
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
            new StubUsbEnumerator()
        );

        var items = manager.GetAll();

        Assert.Empty(items);
    }

    [Fact]
    public void GetAll_WithSubsetOfHandlers_ReturnsCorrectCount()
    {
        var manager = new DeviceManager(
            new IDeviceHandler[] { TestHandlers.Cnvs(), TestHandlers.FanHub() },
            new StubUsbEnumerator()
        );

        var items = manager.GetAll();

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Id == "cnvs");
        Assert.Contains(items, i => i.Id == "fan-hub");
        Assert.DoesNotContain(items, i => i.Id == "qseries");
    }
}
