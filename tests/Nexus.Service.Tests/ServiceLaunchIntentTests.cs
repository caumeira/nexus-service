using Nexus.Service.Platform;

namespace Nexus.Service.Tests;

public class ServiceLaunchIntentTests
{
    [Fact]
    public void ResolveServiceUrl_DefaultsWhenNoArgs()
    {
        Assert.Equal(ServiceLaunchIntent.DefaultBindUrl, ServiceLaunchIntent.ResolveServiceUrl(Array.Empty<string>()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nexus://start")]
    [InlineData("NEXUS://open")]
    public void ResolveServiceUrl_DefaultsForProtocolOrBlankArgs(string arg)
    {
        Assert.Equal(ServiceLaunchIntent.DefaultBindUrl, ServiceLaunchIntent.ResolveServiceUrl(new[] { arg }));
    }

    [Fact]
    public void ResolveServiceUrl_KeepsExplicitBindUrl()
    {
        Assert.Equal("http://127.0.0.1:9500", ServiceLaunchIntent.ResolveServiceUrl(new[] { "http://127.0.0.1:9500" }));
    }

    [Theory]
    [InlineData("http://0.0.0.0:9400", 9400)]
    [InlineData("http://127.0.0.1:9500", 9500)]
    [InlineData("https://localhost:9443", 9443)]
    public void ResolveServicePort_ReadsExplicitPort(string url, int expectedPort)
    {
        Assert.Equal(expectedPort, ServiceLaunchIntent.ResolveServicePort(url));
    }

    [Fact]
    public void ResolveServicePort_FallsBackForInvalidUrl()
    {
        Assert.Equal(ServiceLaunchIntent.DefaultServicePort, ServiceLaunchIntent.ResolveServicePort("not a url"));
    }

    [Theory]
    [InlineData(9500, "http://localhost:9500/")]
    [InlineData(0, "http://localhost:9400/")]
    [InlineData(-1, "http://localhost:9400/")]
    public void LocalDashboardUrl_UsesLocalhostAndFallbackPort(int port, string expectedUrl)
    {
        Assert.Equal(expectedUrl, ServiceLaunchIntent.LocalDashboardUrl(port));
    }
}
