using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Tests.Keeb;

public class KeebReactiveRendererTests
{
    private static KeebReactiveRenderer MakeRenderer(string mode = "SingleKey", RgbColor? color = null)
    {
        var r = new KeebReactiveRenderer();
        r.Configure(enabled: true, mode, color ?? new RgbColor(255, 0, 0));
        return r;
    }

    // ESC: fwRow=0, fwCol=0, commandsIndex=1, wireSlot=0, KeyWireValues[0]=0 -> ledIndex=0.
    private const int EscFwRow = 0;
    private const int EscFwCol = 0;

    // 'A': fwRow=3, fwCol=1, commandsIndex=65, wireSlot=64.
    private const int AFwRow = 3;
    private const int AFwCol = 1;

    // 'H': fwRow=3, fwCol=6, commandsIndex=70, wireSlot=69, gridX=8.
    private const int HFwRow = 3;
    private const int HFwCol = 6;

    [Fact]
    public void SingleKey_PressUnknownKey_NoReaction()
    {
        var renderer = MakeRenderer();
        renderer.IngestKeyPress(99, 99);
        var result = renderer.Render();
        Assert.Null(result);
    }

    [Fact]
    public void SingleKey_PressKnownKey_LedIndexAtFullBrightness()
    {
        var renderer = MakeRenderer(color: new RgbColor(255, 0, 0));
        renderer.IngestKeyPress(EscFwRow, EscFwCol);
        var result = renderer.Render();
        Assert.NotNull(result);
        // ESC ledIndex = 0 (KeyWireValues[0] = 0 = wireSlot for commandsIndex 1).
        Assert.NotNull(result![0]);
        Assert.Equal(255, result[0]!.Value.R);
        Assert.Equal(0, result[0]!.Value.G);
        Assert.Equal(0, result[0]!.Value.B);
    }

    [Fact]
    public void SingleKey_FadesOver10Frames()
    {
        var renderer = MakeRenderer();
        renderer.IngestKeyPress(EscFwRow, EscFwCol);
        // 10 Render() calls advance the reaction through its full lifetime.
        for (var i = 0; i < 10; i++)
        {
            renderer.Render();
        }
        var result = renderer.Render();
        Assert.Null(result);
    }

    [Theory]
    [InlineData(0, 255)] // frame 0: step 10, full brightness
    [InlineData(5, 127)] // frame 5: step 5, 255*5/10=127 (integer truncation)
    [InlineData(9, 25)]  // frame 9: step 1, 255*1/10=25 (integer truncation)
    public void SingleKey_FrameBrightnessDecays(int frameIndex, int expectedR)
    {
        var renderer = MakeRenderer(color: new RgbColor(255, 0, 0));
        renderer.IngestKeyPress(EscFwRow, EscFwCol);
        for (var i = 0; i < frameIndex; i++)
        {
            renderer.Render();
        }
        var result = renderer.Render();
        Assert.NotNull(result);
        Assert.NotNull(result![0]);
        Assert.Equal(expectedR, result[0]!.Value.R);
    }

    [Fact]
    public void ModeChange_ClearsActiveReactions()
    {
        var renderer = new KeebReactiveRenderer();
        renderer.Configure(enabled: true, "SingleKey", new RgbColor(255, 0, 0));
        renderer.IngestKeyPress(EscFwRow, EscFwCol);
        renderer.Render(); // prime the active list

        // Changing mode clears reactions.
        renderer.Configure(enabled: true, "HorizontalLine", new RgbColor(255, 0, 0));
        var result = renderer.Render();
        Assert.Null(result);
    }

    [Fact]
    public void HorizontalLine_Frame0_OriginOnly()
    {
        // 'A' at gridY=6, gridX=3.
        var renderer = MakeRenderer("HorizontalLine");
        renderer.IngestKeyPress(AFwRow, AFwCol);
        var result = renderer.Render();
        Assert.NotNull(result);

        // At frame 0 only the origin column is lit (minDist=0, maxDist=0).
        // 'A': commandsIndex=65, wireSlot=64.
        var aLedIndex = Array.IndexOf(KeebKeyMap.Ansi.WireValues, 64);
        Assert.True(aLedIndex >= 0);
        Assert.NotNull(result![aLedIndex]);

        // 'S' at gridY=6, gridX=4 (dist=1) should NOT be lit at frame 0.
        // commandsIndex=66, wireSlot=65.
        var sLedIndex = Array.IndexOf(KeebKeyMap.Ansi.WireValues, 65);
        if (sLedIndex >= 0)
        {
            Assert.Null(result[sLedIndex]);
        }
    }

    [Fact]
    public void HorizontalLine_Frame3_GapAtCenter()
    {
        // 'H' at gridY=6, gridX=8. At frame 3 (= lineMovement):
        // minDist = max(0, 3 - 2) = 1, maxDist = 3.
        // Origin column (dist=0) is in the gap.
        var renderer = MakeRenderer("HorizontalLine");
        renderer.IngestKeyPress(HFwRow, HFwCol);

        // Advance to frame 3.
        renderer.Render(); // frame 0 -> 1
        renderer.Render(); // frame 1 -> 2
        renderer.Render(); // frame 2 -> 3
        var result = renderer.Render(); // render at frame 3
        Assert.NotNull(result);

        // 'H' at gridX=8: commandsIndex=70, wireSlot=69.
        var hLedIndex = Array.IndexOf(KeebKeyMap.Ansi.WireValues, 69);
        Assert.True(hLedIndex >= 0);
        Assert.Null(result![hLedIndex]);

        // 'K' at gridY=6, gridX=10 (dist=2, in [1,3]): commandsIndex=72, wireSlot=71.
        var kLedIndex = Array.IndexOf(KeebKeyMap.Ansi.WireValues, 71);
        if (kLedIndex >= 0)
        {
            Assert.NotNull(result[kLedIndex]);
        }
    }

    [Fact]
    public void Mask_False_ReactingKey_ShowsReactiveColor()
    {
        var renderer = MakeRenderer(color: new RgbColor(0, 255, 0));
        renderer.IngestKeyPress(EscFwRow, EscFwCol);
        var reactive = renderer.Render();
        Assert.NotNull(reactive);

        var keyBuf = new RgbColor[KeebKeyMap.Ansi.LedCount];
        ApplyReactiveNonMask(keyBuf, reactive!);

        Assert.Equal(0, keyBuf[0].R);
        Assert.Equal(255, keyBuf[0].G);
        Assert.Equal(0, keyBuf[0].B);
    }

    [Fact]
    public void Mask_True_ReactingKey_ShowsBase()
    {
        var renderer = MakeRenderer(color: new RgbColor(0, 255, 0));
        renderer.IngestKeyPress(EscFwRow, EscFwCol);
        var reactive = renderer.Render();
        Assert.NotNull(reactive);

        // Base has a non-black color at ledIndex 0.
        var keyBuf = new RgbColor[KeebKeyMap.Ansi.LedCount];
        keyBuf[0] = new RgbColor(100, 100, 100);
        ApplyReactiveMask(keyBuf, reactive!);

        // Mask: reacting LED keeps base (unchanged).
        Assert.Equal(100, keyBuf[0].R);
        Assert.Equal(100, keyBuf[0].G);
        Assert.Equal(100, keyBuf[0].B);
    }

    [Fact]
    public void Mask_True_NonReactingKey_GoesBlack()
    {
        var renderer = MakeRenderer(color: new RgbColor(0, 255, 0));
        renderer.IngestKeyPress(EscFwRow, EscFwCol);
        var reactive = renderer.Render();
        Assert.NotNull(reactive);

        var keyBuf = new RgbColor[KeebKeyMap.Ansi.LedCount];
        keyBuf[5] = new RgbColor(200, 200, 200);
        ApplyReactiveMask(keyBuf, reactive!);

        // Non-reacting LED goes black in mask mode.
        Assert.Equal(0, keyBuf[5].R);
        Assert.Equal(0, keyBuf[5].G);
        Assert.Equal(0, keyBuf[5].B);
    }

    [Fact]
    public void Ripple_Frame1_OriginLit()
    {
        // 'H' at gridY=6, gridX=8. At frame 1: ring [0, 1) -> dist in [0, 1) -> origin (dist=0) lit.
        var renderer = MakeRenderer("Ripple");
        renderer.IngestKeyPress(HFwRow, HFwCol);

        renderer.Render(); // frame 0 (ring [0,0) = empty, advances to frame 1)
        var result = renderer.Render(); // render at frame 1

        Assert.NotNull(result);
        // 'H': commandsIndex=70, wireSlot=69.
        var hLedIndex = Array.IndexOf(KeebKeyMap.Ansi.WireValues, 69);
        Assert.True(hLedIndex >= 0);
        Assert.NotNull(result![hLedIndex]);
    }

    [Fact]
    public void Ripple_Frame7_OriginDark()
    {
        // At frame 7 (> RippleInnerSize=6): ringMin = 7 - 6 = 1, ringMax = 7.
        // Origin (dist=0) is NOT in [1, 7) -> should be dark.
        var renderer = MakeRenderer("Ripple");
        renderer.IngestKeyPress(HFwRow, HFwCol);

        for (var i = 0; i < 7; i++)
        {
            renderer.Render();
        }
        var result = renderer.Render(); // render at frame 7

        // 'H': commandsIndex=70, wireSlot=69.
        var hLedIndex = Array.IndexOf(KeebKeyMap.Ansi.WireValues, 69);
        Assert.True(hLedIndex >= 0);

        if (result != null)
        {
            Assert.Null(result[hLedIndex]);
        }
    }

    // Helpers mirroring the mask logic in KeebLightingFrameWriter.ApplyReactive.
    private static void ApplyReactiveNonMask(RgbColor[] keyBuf, RgbColor?[] reactive)
    {
        for (var i = 0; i < keyBuf.Length && i < reactive.Length; i++)
        {
            if (reactive[i] is { } c)
            {
                keyBuf[i] = c;
            }
        }
    }

    private static void ApplyReactiveMask(RgbColor[] keyBuf, RgbColor?[] reactive)
    {
        for (var i = 0; i < keyBuf.Length && i < reactive.Length; i++)
        {
            if (!reactive[i].HasValue)
            {
                keyBuf[i] = default;
            }
        }
    }
}
