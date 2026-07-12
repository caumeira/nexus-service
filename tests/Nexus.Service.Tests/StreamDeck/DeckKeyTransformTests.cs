using Nexus.Service.Peripherals.StreamDeck;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>
/// Vectors ported verbatim from nexus-web's deckKeyTransform.test.ts, so the
/// C# and TS transforms are provably computing byte-identical results for
/// the same inputs.
/// </summary>
public class DeckKeyTransformTests
{
    // 2x2 RGBA image, each pixel a distinct color:
    //   TL=red    TR=green
    //   BL=blue   BR=white
    private static DeckRawImage MakeSquare() => new(2, 2, new byte[]
    {
        255, 0, 0, 255, 0, 255, 0, 255,
        0, 0, 255, 255, 255, 255, 255, 255,
    });

    private static byte[] Pixel(DeckRawImage img, int x, int y)
    {
        var o = (y * img.Width + x) * 4;
        return new[] { img.Data[o], img.Data[o + 1], img.Data[o + 2], img.Data[o + 3] };
    }

    [Fact]
    public void ParseTransform_MapsWireStrings()
    {
        Assert.Equal(DeckKeyTransform.None, DeckKeyTransformer.ParseTransform("none"));
        Assert.Equal(DeckKeyTransform.FlipBoth, DeckKeyTransformer.ParseTransform("flipBoth"));
        Assert.Equal(DeckKeyTransform.MirrorXRot90, DeckKeyTransformer.ParseTransform("mirrorXRot90"));
        Assert.Equal(DeckKeyTransform.None, DeckKeyTransformer.ParseTransform("not-a-transform"));
        Assert.Equal(DeckKeyTransform.None, DeckKeyTransformer.ParseTransform(null));
    }

    [Fact]
    public void ApplyKeyTransform_None_IsAPassThrough()
    {
        var img = MakeSquare();
        var outImg = DeckKeyTransformer.ApplyKeyTransform(img, DeckKeyTransform.None);
        Assert.Equal(img.Data, outImg.Data);
    }

    [Fact]
    public void ApplyKeyTransform_FlipBoth_ReversesThePixelOrder()
    {
        var outImg = DeckKeyTransformer.ApplyKeyTransform(MakeSquare(), DeckKeyTransform.FlipBoth);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(outImg, 0, 0)); // was BR (white)
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(outImg, 1, 0)); // was BL (blue)
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(outImg, 0, 1)); // was TR (green)
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(outImg, 1, 1)); // was TL (red)
    }

    [Fact]
    public void ApplyKeyTransform_MirrorXRot90_MirrorsHorizontallyThenRotatesCcw()
    {
        var outImg = DeckKeyTransformer.ApplyKeyTransform(MakeSquare(), DeckKeyTransform.MirrorXRot90);
        Assert.Equal(2, outImg.Width);
        Assert.Equal(2, outImg.Height);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(outImg, 0, 0)); // red
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(outImg, 1, 0)); // blue
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(outImg, 0, 1)); // green
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(outImg, 1, 1)); // white
    }

    [Fact]
    public void ApplyKeyTransform_Rotate90_SwapsWidthHeightForANonSquareImage()
    {
        var wide = new DeckRawImage(3, 1, new byte[]
        {
            255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255,
        });
        var outImg = DeckKeyTransformer.ApplyKeyTransform(wide, DeckKeyTransform.MirrorXRot90);
        Assert.Equal(1, outImg.Width);
        Assert.Equal(3, outImg.Height);
    }

    [Fact]
    public void ApplyOrientation_Zero_IsAPassThrough()
    {
        var img = MakeSquare();
        var outImg = DeckKeyTransformer.ApplyOrientation(img, 0);
        Assert.Equal(img.Data, outImg.Data);
    }

    [Fact]
    public void ApplyOrientation_180_ReversesThePixelOrder()
    {
        var outImg = DeckKeyTransformer.ApplyOrientation(MakeSquare(), 180);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(outImg, 0, 0)); // was BR (white)
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(outImg, 1, 1)); // was TL (red)
    }

    [Fact]
    public void ApplyOrientation_90_CounterRotatesTheContentCounterclockwise()
    {
        var outImg = DeckKeyTransformer.ApplyOrientation(MakeSquare(), 90);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(outImg, 0, 0)); // green (was TR)
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(outImg, 1, 0)); // white (was BR)
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(outImg, 0, 1)); // red (was TL)
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(outImg, 1, 1)); // blue (was BL)
    }

    [Fact]
    public void ApplyOrientation_270_CounterRotatesTheContentClockwise()
    {
        var outImg = DeckKeyTransformer.ApplyOrientation(MakeSquare(), 270);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(outImg, 0, 0)); // blue (was BL)
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(outImg, 1, 0)); // red (was TL)
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(outImg, 0, 1)); // white (was BR)
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(outImg, 1, 1)); // green (was TR)
    }

    [Fact]
    public void ApplyOrientation_Rotate90_SwapsWidthHeightForANonSquareImage()
    {
        var wide = new DeckRawImage(3, 1, new byte[]
        {
            255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255,
        });
        var outImg = DeckKeyTransformer.ApplyOrientation(wide, 90);
        Assert.Equal(1, outImg.Width);
        Assert.Equal(3, outImg.Height);
    }

    [Fact]
    public void ApplyOrientation_UnrecognizedDegrees_IsAPassThrough()
    {
        var img = MakeSquare();
        var outImg = DeckKeyTransformer.ApplyOrientation(img, 45);
        Assert.Equal(img.Data, outImg.Data);
    }

    [Fact]
    public void ComposedUserOrientationAndModelWireTransform_RotatesContentByOrientationBeforeTheModelTransformRuns()
    {
        var rotated = DeckKeyTransformer.ApplyOrientation(MakeSquare(), 270);
        var composed = DeckKeyTransformer.ApplyKeyTransform(rotated, DeckKeyTransform.FlipBoth);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(composed, 0, 0)); // green
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(composed, 1, 0)); // white
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(composed, 0, 1)); // red
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(composed, 1, 1)); // blue
    }
}
