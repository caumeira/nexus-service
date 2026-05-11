using Qos.Service.Platform.Displays;

namespace Qos.Service.Tests;

public class StubDisplayBrightnessProviderTests
{
    [Fact]
    public void Enumerate_ReturnsEmpty()
    {
        var provider = new StubDisplayBrightnessProvider();
        Assert.Empty(provider.Enumerate());
    }

    [Fact]
    public void Hint_DefaultsToEmpty()
    {
        var provider = new StubDisplayBrightnessProvider();
        Assert.Equal(string.Empty, provider.Hint);
    }

    [Fact]
    public void GetBrightness_ReturnsNullForAnyId()
    {
        var provider = new StubDisplayBrightnessProvider();
        Assert.Null(provider.GetBrightness("anything"));
    }

    [Fact]
    public void SetBrightness_ReturnsUnsupportedResultForAnyId()
    {
        var provider = new StubDisplayBrightnessProvider();
        var result = provider.SetBrightness("anything", 50);

        Assert.Equal("anything", result.Id);
        Assert.Equal(50, result.RequestedBrightness);
        Assert.Equal("unsupported", result.Status);
    }

    [Fact]
    public void SetBrightness_ClampsRequest()
    {
        var provider = new StubDisplayBrightnessProvider();
        var result = provider.SetBrightness("anything", 500);

        Assert.Equal(100, result.RequestedBrightness);
    }

    [Fact]
    public void GetBrightnessWritePolicy_ReturnsUnsupportedPolicy()
    {
        var provider = new StubDisplayBrightnessProvider();
        var policy = provider.GetBrightnessWritePolicy("anything");

        Assert.Equal("unsupported", policy.ControlPath);
        Assert.Equal("unsupported", policy.WriteMode);
    }

    [Fact]
    public void GetVcp_ReturnsNullForAnyId()
    {
        var provider = new StubDisplayBrightnessProvider();
        Assert.Null(provider.GetVcp("anything", 0x10));
    }

    [Fact]
    public void SetVcp_ReturnsFalseForAnyId()
    {
        var provider = new StubDisplayBrightnessProvider();
        Assert.False(provider.SetVcp("anything", 0x10, 50));
    }
}
