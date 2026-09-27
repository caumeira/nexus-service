using Nexus.Service.Store;
using Xunit;

namespace Nexus.Service.Tests.Store;

/// <summary>The catalog query carries the dashboard's language only when it is a well-formed tag, so nothing else reaches the cloud URL.</summary>
public sealed class StoreCatalogProxyQueryTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("fur")]
    [InlineData("pt-BR")]
    [InlineData("zh-TW")]
    [InlineData("zh-Hant")]
    public void A_language_tag_is_forwarded(string tag)
    {
        Assert.Equal($"?nexusVersion=3.0.19&locale={tag}", StoreCatalogProxy.Query("3.0.19", null, tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EN")]
    [InlineData("e")]
    [InlineData("pt_BR")]
    [InlineData("pt-BR-x")]
    [InlineData("en&touch=false")]
    [InlineData("de-%41")]
    public void Anything_else_is_left_out(string? tag)
    {
        Assert.Equal("?nexusVersion=3.0.19&touch=true", StoreCatalogProxy.Query("3.0.19", true, tag));
    }
}
