using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting;

/// <summary>
/// Invariant: every default canvas-frame slot must land inside the 1000×600
/// unit canvas with at least PAD=12 inset from every edge. The web-side drag
/// clamp uses exactly that PAD, so a default position outside it snaps on the
/// user's first interaction and the persisted layout drifts. These tests guard
/// against the regression the previous "newly-attached device disappears off
/// the bottom" bug was about.
/// </summary>
public class CanvasDefaultLayoutTests
{
    private const float CanvasW = 1000f;
    private const float CanvasH = 600f;
    private const float Pad = 12f;

    [Theory]
    [InlineData(0)]
    [InlineData(17)]   // last slot before wrap (Cols=3 × Rows=6 = 18)
    [InlineData(18)]   // wraps back to slot 0
    [InlineData(35)]
    [InlineData(99)]
    public void DefaultCardLayout_stays_inside_drag_clamp(int slot)
    {
        var (x, y, w, h) = OpenRgbLightingDeviceProvider.DefaultCardLayout(slot);
        AssertInsideCanvas(x, y, w, h);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]    // last slot before wrap (Cols=2 × Rows=3 = 6)
    [InlineData(6)]    // wraps
    [InlineData(11)]
    [InlineData(50)]
    public void DefaultStripLayout_stays_inside_drag_clamp(int slot)
    {
        var (x, y, w, h) = OpenRgbLightingDeviceProvider.DefaultStripLayout(slot);
        AssertInsideCanvas(x, y, w, h);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]    // last slot before wrap (Cols=4 × Rows=2 = 8)
    [InlineData(8)]    // wraps
    [InlineData(15)]
    [InlineData(99)]
    public void DefaultNp50Layout_stays_inside_drag_clamp(int slot)
    {
        var (x, y, w, h) = Np50LightingDeviceProvider.DefaultNp50Layout(slot);
        AssertInsideCanvas(x, y, w, h);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]    // last slot before wrap (Cols=4 × Rows=2 = 8)
    [InlineData(8)]    // wraps
    [InlineData(15)]
    [InlineData(99)]
    public void DefaultMiniHubLayout_stays_inside_drag_clamp(int slot)
    {
        var (x, y, w, h) = MiniHubLightingDeviceProvider.DefaultMiniHubLayout(slot);
        AssertInsideCanvas(x, y, w, h);
    }

    private static void AssertInsideCanvas(float x, float y, float w, float h)
    {
        Assert.InRange(x, Pad, CanvasW - Pad - w);
        Assert.InRange(y, Pad, CanvasH - Pad - h);
    }
}
