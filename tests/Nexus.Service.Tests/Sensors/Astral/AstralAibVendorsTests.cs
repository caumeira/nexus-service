using Nexus.Service.Sensors.Astral;

namespace Nexus.Service.Tests.Sensors.Astral;

public class AstralAibVendorsTests
{
    [Theory]
    [InlineData(0x89DE1043u, "ASUS")]
    [InlineData(0x12341462u, "MSI")]
    [InlineData(0xABCD1458u, "Gigabyte")]
    [InlineData(0x00013842u, "EVGA")]
    [InlineData(0x000119DAu, "Zotac")]
    [InlineData(0x00011569u, "Palit")]
    [InlineData(0x000110B0u, "Gainward")]
    [InlineData(0x0001196Eu, "PNY")]
    [InlineData(0x00011DA2u, "Sapphire")]
    public void TryGetBrand_resolves_known_vendor_ids(uint subSystemId, string expected)
    {
        Assert.True(AstralAibVendors.TryGetBrand(subSystemId, out var brand));
        Assert.Equal(expected, brand);
    }

    [Fact]
    public void TryGetBrand_returns_false_for_nvidia_founders_edition()
    {
        Assert.False(AstralAibVendors.TryGetBrand(0x000110DEu, out _));
    }

    [Fact]
    public void TryGetBrand_returns_false_for_unrecognized_vendor()
    {
        Assert.False(AstralAibVendors.TryGetBrand(0x00019999u, out _));
    }

    [Fact]
    public void EnrichName_prefixes_known_aib_brand_and_keeps_geforce()
    {
        var result = AstralAibVendors.EnrichName("NVIDIA GeForce RTX 5080", 0x89DE1043u, isAstral: false);
        Assert.Equal("ASUS GeForce RTX 5080", result);
    }

    [Fact]
    public void EnrichName_uses_rog_astral_prefix_and_drops_geforce_when_confirmed()
    {
        var result = AstralAibVendors.EnrichName("NVIDIA GeForce RTX 5080", 0x89DE1043u, isAstral: true);
        Assert.Equal("ASUS ROG Astral RTX 5080", result);
    }

    [Fact]
    public void EnrichName_leaves_founders_edition_unchanged()
    {
        var result = AstralAibVendors.EnrichName("NVIDIA GeForce RTX 5080", 0x000110DEu, isAstral: false);
        Assert.Equal("NVIDIA GeForce RTX 5080", result);
    }

    [Fact]
    public void EnrichName_leaves_unrecognized_vendor_unchanged()
    {
        var result = AstralAibVendors.EnrichName("NVIDIA GeForce RTX 5080", 0x00019999u, isAstral: false);
        Assert.Equal("NVIDIA GeForce RTX 5080", result);
    }
}
