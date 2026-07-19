using Nexus.Service.Media;

namespace Nexus.Service.Tests;

public class CropRectTests
{
    [Fact]
    public void TryParse_LegacyFourFields_HasNoOrientation()
    {
        Assert.True(CropRect.TryParse("0.1,0.2,0.5,0.4", out var c));
        Assert.Equal(0, c.Rotate);
        Assert.False(c.Mirror);
        // Legacy strings must render byte-for-byte the pre-rotation filter.
        Assert.Equal("crop=iw*0.5:ih*0.4:iw*0.1:ih*0.2", c.ToFfmpegCrop());
    }

    [Fact]
    public void TryParse_SixFields_CarriesRotateAndMirror()
    {
        Assert.True(CropRect.TryParse("0,0,1,1,90,1", out var c));
        Assert.Equal(90, c.Rotate);
        Assert.True(c.Mirror);
    }

    [Theory]
    [InlineData(0, false, "")]
    [InlineData(90, false, "transpose=1,")]
    [InlineData(180, false, "transpose=1,transpose=1,")]
    [InlineData(270, false, "transpose=2,")]
    [InlineData(0, true, "hflip,")]
    [InlineData(90, true, "hflip,transpose=1,")]
    public void OrientationFilter_MirrorThenRotate(int rotate, bool mirror, string expected)
    {
        Assert.Equal(expected, CropRect.OrientationFilter(rotate, mirror));
    }

    [Fact]
    public void ToFfmpegCrop_AppliesOrientationBeforeCrop()
    {
        Assert.True(CropRect.TryParse("0.25,0,0.5,1,90,0", out var c));
        Assert.Equal("transpose=1,crop=iw*0.5:ih*1:iw*0.25:ih*0", c.ToFfmpegCrop());
    }

    [Theory]
    [InlineData("0,0,1,1,45,0")]   // rotation not a right angle
    [InlineData("0,0,1,1,90,2")]   // mirror not 0/1
    [InlineData("0,0,1,1,90")]     // wrong field count
    [InlineData("0,0,1,1,x,0")]    // non-numeric rotation
    public void TryParse_RejectsMalformedOrientation(string raw)
    {
        Assert.False(CropRect.TryParse(raw, out _));
    }

    [Theory]
    [InlineData("0,0,1,1", true)]
    [InlineData("0,0,1,1,0,0", true)]
    [InlineData("0,0,1,1,90,0", false)]
    [InlineData("0,0,1,1,0,1", false)]
    [InlineData("0.1,0,0.5,1", false)]
    public void IsIdentity_OnlyWhenFullFrameAndNoOrientation(string raw, bool expected)
    {
        Assert.True(CropRect.TryParse(raw, out var c));
        Assert.Equal(expected, c.IsIdentity);
    }
}
