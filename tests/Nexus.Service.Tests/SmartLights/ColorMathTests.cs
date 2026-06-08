using Nexus.Service.Lighting.Smart;
using Xunit;

namespace Nexus.Service.Tests.SmartLights;

public class ColorMathTests
{
    [Theory]
    [InlineData(0f, 1f, 1f, 255, 0, 0)]       // red
    [InlineData(1f / 3f, 1f, 1f, 0, 255, 0)]  // green
    [InlineData(2f / 3f, 1f, 1f, 0, 0, 255)]  // blue
    public void HsvToRgb_primaries(float h, float s, float v, int r, int g, int b)
    {
        var (rr, gg, bb) = ColorMath.HsvToRgb(h, s, v);
        Assert.Equal(r, rr);
        Assert.Equal(g, gg);
        Assert.Equal(b, bb);
    }

    [Fact]
    public void HsvToRgb_zeroSaturation_isGray()
    {
        var (r, g, b) = ColorMath.HsvToRgb(0.5f, 0f, 0.5f);
        Assert.Equal(r, g);
        Assert.Equal(g, b);
        Assert.InRange((int)r, 126, 130);
    }

    [Fact]
    public void RgbToXy_white_isNearD65()
    {
        var (x, y) = ColorMath.RgbToXy(255, 255, 255);
        Assert.InRange(x, 0.30, 0.33);
        Assert.InRange(y, 0.31, 0.34);
    }

    [Fact]
    public void RgbToXy_black_isZero()
    {
        var (x, y) = ColorMath.RgbToXy(0, 0, 0);
        Assert.Equal(0.0, x);
        Assert.Equal(0.0, y);
    }

    [Fact]
    public void RgbToXy_red_isInRedRegion()
    {
        var (x, y) = ColorMath.RgbToXy(255, 0, 0);
        Assert.True(x > 0.6, $"red x should be >0.6, got {x}");
        Assert.True(y < 0.4, $"red y should be <0.4, got {y}");
    }

    [Fact]
    public void Value_isMaxChannel()
    {
        Assert.Equal(1f, ColorMath.Value(255, 128, 0));
        Assert.Equal(0f, ColorMath.Value(0, 0, 0));
        Assert.InRange(ColorMath.Value(128, 0, 0), 0.49f, 0.51f);
    }

    [Fact]
    public void KelvinToMirek_clampsToHueRange()
    {
        Assert.Equal(200, ColorMath.KelvinToMirek(5000));
        Assert.Equal(153, ColorMath.KelvinToMirek(100000)); // clamp low
        Assert.Equal(500, ColorMath.KelvinToMirek(1000));   // clamp high
    }
}
