using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Protocols.Corsair;
using Xunit;

namespace Nexus.Service.Tests;

public class CorsairFactoryTests
{
    [Fact]
    public void Supports_KnownCorsairPids()
    {
        Assert.True(CorsairPeripheralFactory.Supports(0x1B2E));  // M65 Pro RGB
        Assert.True(CorsairPeripheralFactory.Supports(0x1B4C));  // Dark Core RGB Pro
        Assert.True(CorsairPeripheralFactory.Supports(0x1B6D));  // K70 RGB Pro
    }

    [Fact]
    public void Supports_UnknownPidReturnsFalse()
    {
        Assert.False(CorsairPeripheralFactory.Supports(0xFFFF));
    }

    [Fact]
    public void TryCreate_ReturnsDetectionShellForM65Pro()
    {
        var factory = new CorsairPeripheralFactory();
        var p = factory.TryCreate(new StubHidEnumeratorNoDevices(), 0x1B2E, "test-serial");

        Assert.NotNull(p);
        Assert.Equal("Corsair", p!.Vendor);
        Assert.Equal("M65 Pro RGB", p.Name);
        Assert.Equal("mouse", p.Category);
        Assert.Equal("test-serial", p.Serial);
        // Per CorsairPeripheralFactory comment: all entries are detection-only
        // because pre-Bragi Corsair uses USB vendor control transfers we can't
        // send via HID without a driver replacement.
        Assert.Empty(p.Capabilities);
    }

    [Fact]
    public void TryCreate_ReturnsNullForUnsupportedPid()
    {
        var factory = new CorsairPeripheralFactory();
        var p = factory.TryCreate(new StubHidEnumeratorNoDevices(), 0xFFFF, "serial");
        Assert.Null(p);
    }

    [Fact]
    public void VendorId_IsCorsair()
    {
        Assert.Equal(0x1B1C, CorsairPeripheralFactory.CorsairVendorId);
    }
}
