using System.Collections.Generic;
using System.Linq;
using Qos.Service.Devices;
using Qos.Service.Devices.Detection;
using Qos.Service.Peripherals;
using Qos.Service.Peripherals.Hid;
using Xunit;

namespace Qos.Service.Tests;

internal sealed class StubHidEnumeratorNoDevices : IHidEnumerator
{
    public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => System.Array.Empty<HidDeviceInfo>();
    public IHidDevice? Open(string path) => null;
}

internal sealed class FakeUsbEnumerator : IUsbEnumerator
{
    public List<UsbDeviceEntry> Devices { get; } = new();
    public List<UsbDeviceEntry> Enumerate() => Devices.ToList();
}

public class PeripheralRegistryTests
{
    [Fact]
    public void GetAll_EmptyWhenNoRecognizedDevices()
    {
        var usb = new FakeUsbEnumerator();
        usb.Devices.Add(new UsbDeviceEntry { VendorId = 0x046D, ProductId = 0xC548, Serial = "abc" });  // Logitech (unsupported)
        var reg = new PeripheralRegistry(usb, new StubHidEnumeratorNoDevices());

        Assert.Empty(reg.GetAll());
    }

    [Fact]
    public void GetAll_CreatesCorsairPeripheralForRecognizedPid()
    {
        var usb = new FakeUsbEnumerator();
        usb.Devices.Add(new UsbDeviceEntry
        {
            VendorId = 0x1B1C,     // Corsair
            ProductId = 0x1B2E,    // M65 Pro RGB
            Serial = "corsair-serial",
        });
        var reg = new PeripheralRegistry(usb, new StubHidEnumeratorNoDevices());

        var list = reg.GetAll();
        Assert.Single(list);
        Assert.Equal("Corsair", list[0].Vendor);
        Assert.Equal("M65 Pro RGB", list[0].Name);
        Assert.Empty(list[0].Capabilities);  // detection-only shell
    }

    [Fact]
    public void GetAll_CachesInstancesAcrossCalls()
    {
        var usb = new FakeUsbEnumerator();
        usb.Devices.Add(new UsbDeviceEntry
        {
            VendorId = 0x1B1C, ProductId = 0x1B2E, Serial = "corsair",
        });
        var reg = new PeripheralRegistry(usb, new StubHidEnumeratorNoDevices());

        var first = reg.GetAll()[0];
        var second = reg.GetAll()[0];
        Assert.Same(first, second);
    }

    [Fact]
    public void GetAll_RemovesDisposedPeripheralsWhenDeviceUnplugs()
    {
        var usb = new FakeUsbEnumerator();
        usb.Devices.Add(new UsbDeviceEntry
        {
            VendorId = 0x1B1C, ProductId = 0x1B2E, Serial = "corsair",
        });
        var reg = new PeripheralRegistry(usb, new StubHidEnumeratorNoDevices());
        Assert.Single(reg.GetAll());

        // Simulate unplug: remove from USB enumerator output.
        usb.Devices.Clear();
        Assert.Empty(reg.GetAll());
    }

    [Fact]
    public void Get_FindsPeripheralByItsLogicalId()
    {
        var usb = new FakeUsbEnumerator();
        usb.Devices.Add(new UsbDeviceEntry
        {
            VendorId = 0x1B1C, ProductId = 0x1B2E, Serial = "lookup",
        });
        var reg = new PeripheralRegistry(usb, new StubHidEnumeratorNoDevices());

        var p = reg.GetAll()[0];
        var looked = reg.Get(p.Id);
        Assert.Same(p, looked);

        Assert.Null(reg.Get("nonexistent-id"));
    }
}
