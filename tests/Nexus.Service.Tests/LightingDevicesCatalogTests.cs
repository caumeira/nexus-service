using System.Linq;
using Nexus.Service.Peripherals;
using Xunit;

namespace Nexus.Service.Tests;

public class LightingDevicesCatalogTests
{
    [Fact]
    public void Catalog_IsLoadedFromEmbeddedOpenRgbResource()
    {
        var all = LightingDevicesCatalog.All;

        Assert.True(all.Count > 1500,
            $"expected the OpenRGB-derived catalog (>1500 devices), got {all.Count} — " +
            "the embedded resource is likely missing or stale");
    }

    [Fact]
    public void Catalog_ContainsKnownHidDeviceWithVidPid()
    {
        var all = LightingDevicesCatalog.All;

        var hyteKeeb = all.FirstOrDefault(d => d.Model.Contains("Keeb TKL"));
        Assert.NotNull(hyteKeeb);
        Assert.Equal("0x3402", hyteKeeb!.VendorId);
        Assert.Equal("0x0300", hyteKeeb.ProductId);
    }

    [Fact]
    public void Catalog_AssignsSensibleCategories()
    {
        var all = LightingDevicesCatalog.All;

        Assert.Contains(all, d => d.Category == "keyboard");
        Assert.Contains(all, d => d.Category == "mouse");
        Assert.Contains(all, d => d.Category == "gpu");
        Assert.Contains(all, d => d.Category == "motherboard");
    }
}
