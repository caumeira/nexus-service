using Nexus.Service.Peripherals.StreamDeck;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>
/// The IStreamDeckSurface contract every implementation must satisfy.
/// Subclassed once per surface (today: <see cref="SimulatedStreamDeckSurfaceContractTests"/>);
/// a future HID test double runs the identical suite against
/// <see cref="HidStreamDeckSurface"/> without duplicating assertions.
/// </summary>
public abstract class StreamDeckSurfaceContractTests
{
    protected abstract IStreamDeckSurface CreateSurface();

    [Fact]
    public void NewSurface_IsConnected()
    {
        using var surface = CreateSurface();
        Assert.True(surface.IsConnected);
    }

    [Fact]
    public void SetBrightness_InRange_Succeeds()
    {
        using var surface = CreateSurface();
        Assert.True(surface.SetBrightness(42));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(150)]
    public void SetBrightness_OutOfRange_StillSucceeds_ClampedInternally(int percent)
    {
        using var surface = CreateSurface();
        Assert.True(surface.SetBrightness(percent));
    }

    [Fact]
    public void SetKeyImage_ValidIndex_Succeeds()
    {
        using var surface = CreateSurface();
        Assert.True(surface.SetKeyImage(0, new byte[] { 1, 2, 3 }));
    }

    [Theory]
    [InlineData(-1)]
    public void SetKeyImage_NegativeIndex_Fails(int index)
    {
        using var surface = CreateSurface();
        Assert.False(surface.SetKeyImage(index, new byte[] { 1 }));
    }

    [Fact]
    public void SetKeyImage_IndexAtOrAboveKeyCount_Fails()
    {
        using var surface = CreateSurface();
        Assert.False(surface.SetKeyImage(surface.Model.KeyCount, new byte[] { 1 }));
    }

    [Fact]
    public void Reset_Succeeds()
    {
        using var surface = CreateSurface();
        Assert.True(surface.Reset());
    }

    [Fact]
    public void ReadInput_IdleReturnsNull()
    {
        using var surface = CreateSurface();
        Assert.Null(surface.ReadInput(10));
    }
}
