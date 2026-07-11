using Nexus.Service.Devices;
using Nexus.Service.Devices.Handlers;
using Xunit;

namespace Nexus.Service.Tests.Devices;

public class DeviceControlPolicyTests
{
    [Fact]
    public void ConflictAppFor_StreamDeck_ReturnsTheElgatoCatalogId()
    {
        Assert.Equal(StreamDeckHandler.ElgatoConflictAppId, DeviceControlPolicy.ConflictAppFor("streamdeck"));
    }

    [Fact]
    public void DefaultOn_StreamDeck_IsFalse()
    {
        // Nexus Link defaults off while a competing vendor app (Elgato's own
        // Stream Deck software) may also be driving the same device.
        Assert.False(DeviceControlPolicy.DefaultOn("streamdeck"));
    }

    [Theory]
    [InlineData("lianli")]
    [InlineData("lianli-tl")]
    [InlineData("lianli-wireless")]
    [InlineData("lianli-aio")]
    [InlineData("strimer")]
    [InlineData("corsair")]
    [InlineData("tryx")]
    [InlineData("streamdeck")]
    public void ConflictAppFor_KnownHandlers_ReturnsANonEmptyId(string handlerId)
    {
        Assert.False(string.IsNullOrEmpty(DeviceControlPolicy.ConflictAppFor(handlerId)));
    }

    [Fact]
    public void ConflictAppFor_UnmappedHandler_ReturnsNull()
    {
        Assert.Null(DeviceControlPolicy.ConflictAppFor("keeb"));
    }

    [Fact]
    public void DefaultOn_UnmappedHandler_IsTrue()
    {
        Assert.True(DeviceControlPolicy.DefaultOn("keeb"));
    }
}
