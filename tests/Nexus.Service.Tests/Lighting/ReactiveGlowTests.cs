using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

public class ReactiveGlowTests
{
    [Fact]
    public void BandCount_IsK()
    {
        var glow = new ReactiveGlow();
        Assert.Equal(12, glow.BandCount);
        Assert.Equal(12 * 3, glow.BandFloats.Length);
    }

    [Fact]
    public void Advance_WithoutIngest_DoesNotThrow()
    {
        var glow = new ReactiveGlow();
        glow.Advance(0.5f);

        for (int i = 0; i < glow.BandCount * 3; i++)
        {
            Assert.Equal(0f, glow.BandFloats[i]);
        }
    }

    [Fact]
    public void Ingest_AllRed_ProducesRedBands()
    {
        var glow = new ReactiveGlow();
        var canvas = new CanvasBuffer(160, 90);
        canvas.Fill(200, 20, 20);

        glow.Ingest(canvas, 0.5f);
        glow.Advance(0.5f);

        for (int b = 0; b < glow.BandCount; b++)
        {
            float r = glow.BandFloats[b * 3];
            float g = glow.BandFloats[b * 3 + 1];
            float bl = glow.BandFloats[b * 3 + 2];
            Assert.True(r > g, $"band {b}: r={r} should exceed g={g}");
            Assert.True(r > bl, $"band {b}: r={r} should exceed b={bl}");
        }
    }

    [Fact]
    public void Ingest_BlueLeftRedRight_BandsSplitByPosition()
    {
        var glow = new ReactiveGlow();
        var canvas = new CanvasBuffer(160, 90);
        for (int y = 0; y < canvas.Height; y++)
        {
            for (int x = 0; x < canvas.Width; x++)
            {
                if (x < canvas.Width / 2)
                {
                    canvas.SetPixel(x, y, 20, 20, 200);
                }
                else
                {
                    canvas.SetPixel(x, y, 200, 20, 20);
                }
            }
        }

        // intensity 1 -> palette cap of 5, so both colours survive
        glow.Ingest(canvas, 1f);
        for (int i = 0; i < 12; i++)
        {
            glow.Advance(1f);
        }

        int last = glow.BandCount - 1;
        float leftR = glow.BandFloats[0];
        float leftB = glow.BandFloats[2];
        Assert.True(leftB > leftR, $"leftmost band should be blue: b={leftB} r={leftR}");

        float rightR = glow.BandFloats[last * 3];
        float rightB = glow.BandFloats[last * 3 + 2];
        Assert.True(rightR > rightB, $"rightmost band should be red: r={rightR} b={rightB}");
    }

    [Fact]
    public void Ingest_AllBlack_HoldsLastColors()
    {
        var glow = new ReactiveGlow();
        var canvas = new CanvasBuffer(160, 90);

        // Prime with vivid red
        canvas.Fill(200, 20, 20);
        glow.Ingest(canvas, 0.5f);
        glow.Advance(0.5f);

        float primeR = glow.BandFloats[0];

        // Feed black: hold-on-black applies slow decay, not immediate zero
        canvas.Fill(0, 0, 0);
        glow.Ingest(canvas, 0.5f);
        glow.Advance(0.5f);

        float afterR = glow.BandFloats[0];
        Assert.True(afterR > 0f, $"expected non-zero after hold-on-black, got {afterR}");
        Assert.True(afterR < primeR, $"expected decay: afterR={afterR} primeR={primeR}");
    }

    [Fact]
    public void Ingest_NearWhite_BandFloatsFinite()
    {
        var glow = new ReactiveGlow();
        var canvas = new CanvasBuffer(160, 90);
        // Near-white pixels hit the neutral gate; result must still be finite
        canvas.Fill(230, 230, 230);

        glow.Ingest(canvas, 0.5f);
        glow.Advance(0.5f);

        for (int i = 0; i < glow.BandCount * 3; i++)
        {
            Assert.True(float.IsFinite(glow.BandFloats[i]), $"BandFloats[{i}] is not finite");
        }
    }
}
