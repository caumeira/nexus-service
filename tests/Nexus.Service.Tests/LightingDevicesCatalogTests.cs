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
            $"expected the OpenRGB-derived catalog (>1500 devices), got {all.Count} - " +
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

    [Theory]
    [InlineData("Lian Li", "Uni Fan SL-Infinity", "0x0CF2", "0xA102")]
    [InlineData("Lian Li", "Galahad II Trinity", "0x0416", "0x7373")]
    [InlineData("Lian Li", "SL-LCD", "0x1CBE", "0x0005")]
    [InlineData("Tryx", "Panorama", "0x391A", "0x1011")]
    [InlineData("Corsair", "iCUE LINK System Hub", "0x1B1C", "0x0C3F")]
    public void Catalog_ListsNativelyDrivenFirstPartyDevices(string vendor, string model, string vid, string pid)
    {
        var device = LightingDevicesCatalog.All.SingleOrDefault(
            d => d.Vendor == vendor && d.Model == model);

        Assert.NotNull(device);
        Assert.Equal("nexus", device!.Source);
        Assert.Equal(vid, device.VendorId);
        Assert.Equal(pid, device.ProductId);
    }

    [Fact]
    public void Catalog_SuppressesOpenRgbRowsForNativelyDrivenHardware()
    {
        // Lian Li Uni Fan/Strimer/Galahad and the Corsair iCUE LINK hub also ship in
        // the OpenRGB resource; each VID:PID must survive exactly once, as "nexus".
        var all = LightingDevicesCatalog.All;

        foreach (var (vid, pid) in new[]
                 {
                     ("0x0CF2", "0xA102"), // Uni Fan SL-Infinity
                     ("0x0416", "0x7373"), // Galahad II Trinity
                     ("0x0CF2", "0xA200"), // Strimer
                     ("0x1B1C", "0x0C3F"), // iCUE LINK System Hub
                 })
        {
            var rows = all.Where(d => d.VendorId == vid && d.ProductId == pid).ToList();
            Assert.Single(rows);
            Assert.Equal("nexus", rows[0].Source);
        }
    }
}
