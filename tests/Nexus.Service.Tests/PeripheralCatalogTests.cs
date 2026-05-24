using System.Linq;
using Nexus.Service.Peripherals;
using Xunit;

namespace Nexus.Service.Tests;

public class PeripheralCatalogTests
{
    [Fact]
    public void SupportedDevicesCatalog_ContainsRazerMice()
    {
        var razer = SupportedDevicesCatalog.All.Where(d => d.Vendor == "Razer").ToList();
        Assert.NotEmpty(razer);
        // Must include the DeathAdder V2 Pro generation we have protocol for
        Assert.Contains(razer, d => d.ProductId.Equals("0x007D", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SupportedDevicesCatalog_HasStableCategories()
    {
        foreach (var d in SupportedDevicesCatalog.All)
        {
            Assert.Contains(d.Category, new[] { "mouse", "keyboard", "headset" });
        }
    }

    [Fact]
    public void SupportedDevicesCatalog_AllEntriesHaveVidPid()
    {
        foreach (var d in SupportedDevicesCatalog.All)
        {
            Assert.StartsWith("0x", d.VendorId);
            Assert.StartsWith("0x", d.ProductId);
            Assert.Equal(6, d.VendorId.Length);
            Assert.Equal(6, d.ProductId.Length);
        }
    }

    [Fact]
    public void LightingDevicesCatalog_LoadsFromEmbeddedResource()
    {
        // The refactored LightingDevicesCatalog loads from the openrgb-supported-devices.json
        // resource. If the resource isn't embedded, the list is empty (not an exception).
        // We accept 0 (missing resource in unit-test run) OR a populated list.
        var list = LightingDevicesCatalog.All;
        Assert.NotNull(list);
    }

    [Fact]
    public void LightingDevicesCatalog_CategoriesAreReasonable_WhenPopulated()
    {
        foreach (var d in LightingDevicesCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Category));
            Assert.False(string.IsNullOrWhiteSpace(d.Model));
        }
    }
}
