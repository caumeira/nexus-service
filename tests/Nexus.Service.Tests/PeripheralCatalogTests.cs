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
            Assert.Contains(d.Category, new[] { "mouse", "keyboard", "headset", "controller" });
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
        // LightingDevicesCatalog.All loads from the openrgb-supported-devices.json
        // embedded resource; a missing resource yields an empty list, so a
        // populated catalog is the proof the resource embedded and parsed.
        Assert.NotEmpty(LightingDevicesCatalog.All);
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

    [Fact]
    public void LightingDevicesCatalog_InjectsFirstPartyHyteDevices()
    {
        var hyte = LightingDevicesCatalog.All.Where(d => d.Vendor == "HYTE").ToList();
        Assert.Contains(hyte, d => d.Model == "THICC Q60");
        Assert.Contains(hyte, d => d.Model == "Nexus Portal NP50");
        // The OpenRGB fork's own HYTE rows are suppressed, so every HYTE row is first-party.
        Assert.All(hyte, d => Assert.Equal("nexus", d.Source));
    }

    [Fact]
    public void LightingDevicesCatalog_DropsMislabeledNexusCaseRow()
    {
        // The OpenRGB "HYTE Nexus" detector previously surfaced as model "Nexus"
        // typed "case"; the curated first-party list replaces it.
        Assert.DoesNotContain(LightingDevicesCatalog.All, d => d.Vendor == "HYTE" && d.Model == "Nexus");
        Assert.DoesNotContain(LightingDevicesCatalog.All, d => d.Vendor == "HYTE" && d.Category == "case");
    }

    [Fact]
    public void LightingDevicesCatalog_EverySourceIsKnown()
    {
        Assert.All(LightingDevicesCatalog.All, d => Assert.Contains(d.Source, new[] { "nexus", "openrgb" }));
    }
}
