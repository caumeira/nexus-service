using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;
using Nexus.Service.Peripherals.Hyte.Keeb;

namespace Nexus.Service.Tests.Lighting;

/// <summary>
/// Tests for GameSyncEffect: COLORREF decoding, per-device routing, keyboard
/// grid UV sampling, semantic-peripheral fill, and CHROMA_NONE/CHROMA_STATIC handling.
/// </summary>
public class GameSyncEffectTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    // Build a COLORREF int (0x00BBGGRR) from RGB bytes.
    private static int Colorref(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    // Build a DeviceFrame with per-LED UV positions that mimic the Keeb TKL
    // key matrix: LED i is at column (wireValue % stride), row (wireValue / stride).
    private static DeviceFrame MakeKeyboardFrame(string id, int ledCount,
        int matrixStride, int maxCol, int maxRow)
    {
        var frame = new DeviceFrame(0, id, ledCount)
        {
            Archetype = "keyboard"
        };
        var u = new float[ledCount];
        var v = new float[ledCount];
        // Assign synthetic UV where LED i occupies column i%maxCol, row i/maxCol.
        for (int i = 0; i < ledCount; i++)
        {
            u[i] = maxCol > 0 ? (i % matrixStride) / (float)maxCol : 0.5f;
            v[i] = maxRow > 0 ? (i / matrixStride) / (float)maxRow : 0.5f;
        }
        frame.LedU = u;
        frame.LedV = v;
        return frame;
    }

    // Build a solid-colored Chroma grid of gridRows x gridCols cells, all one color.
    private static int[] SolidGrid(int gridRows, int gridCols, byte r, byte g, byte b)
    {
        var grid = new int[gridRows * gridCols];
        var cr = Colorref(r, g, b);
        for (int i = 0; i < grid.Length; i++) grid[i] = cr;
        return grid;
    }

    // ── COLORREF decoding ────────────────────────────────────────────────────

    [Fact]
    public void IngestFrame_Custom_DecodesColorrefRgbByteOrder()
    {
        var effect = new GameSyncEffect();
        // COLORREF 0x00FFAA11 = R=0x11, G=0xAA, B=0xFF
        var colors = new[] { Colorref(0x11, 0xAA, 0xFF) };
        effect.IngestFrame("keyboard", "CHROMA_CUSTOM", 1, 1, colors);

        // Frame with a single-LED keyboard at U=0, V=0 should pick up the decoded color.
        var frame = new DeviceFrame(0, "kb:keys", 1) { Archetype = "keyboard" };
        frame.LedU = new float[] { 0f };
        frame.LedV = new float[] { 0f };

        effect.WriteToDevices(new[] { frame });

        var leds = frame.LedBytes;
        Assert.Equal(0x11, leds[0]); // R
        Assert.Equal(0xAA, leds[1]); // G
        Assert.Equal(0xFF, leds[2]); // B
    }

    // ── Keyboard grid UV sampling ────────────────────────────────────────────

    [Fact]
    public void WriteToDevices_Keyboard_SamplesGridAtLedUv()
    {
        // 2x2 Chroma grid: top-left=red, top-right=green, bottom-left=blue, bottom-right=white.
        var colors = new[]
        {
            Colorref(255, 0, 0),   // (0,0) top-left
            Colorref(0, 255, 0),   // (0,1) top-right
            Colorref(0, 0, 255),   // (1,0) bottom-left
            Colorref(255, 255, 255), // (1,1) bottom-right
        };

        var effect = new GameSyncEffect();
        effect.IngestFrame("keyboard", "CHROMA_CUSTOM", 2, 2, colors);

        // 4-LED frame: corners at (U,V) = (0,0), (1,0), (0,1), (1,1).
        var frame = new DeviceFrame(0, "kb:keys", 4) { Archetype = "keyboard" };
        frame.LedU = new float[] { 0f, 1f, 0f, 1f };
        frame.LedV = new float[] { 0f, 0f, 1f, 1f };

        effect.WriteToDevices(new[] { frame });

        var leds = frame.LedBytes;
        // LED 0: grid (row=0,col=0) -> red
        Assert.Equal(255, leds[0]); Assert.Equal(0, leds[1]); Assert.Equal(0, leds[2]);
        // LED 1: grid (row=0,col=1) -> green
        Assert.Equal(0, leds[3]); Assert.Equal(255, leds[4]); Assert.Equal(0, leds[5]);
        // LED 2: grid (row=1,col=0) -> blue
        Assert.Equal(0, leds[6]); Assert.Equal(0, leds[7]); Assert.Equal(255, leds[8]);
        // LED 3: grid (row=1,col=1) -> white
        Assert.Equal(255, leds[9]); Assert.Equal(255, leds[10]); Assert.Equal(255, leds[11]);
    }

    [Fact]
    public void WriteToDevices_Keyboard_6x22Grid_MapsExpectedCells()
    {
        // Chroma standard CUSTOM: 6 rows x 22 cols.
        // Color each cell distinctly: cell (r,c) = Colorref(r*10, c*10, 0).
        const int rows = 6, cols = 22;
        var colors = new int[rows * cols];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                colors[r * cols + c] = Colorref((byte)(r * 10), (byte)(c * 10), 0);
            }
        }

        var effect = new GameSyncEffect();
        effect.IngestFrame("keyboard", "CHROMA_CUSTOM", rows, cols, colors);

        // A keyboard LED at U=0.5 (col ~10.5 -> rounds to 11), V=0.333 (row ~2).
        var frame = new DeviceFrame(0, "kb:keys", 1) { Archetype = "keyboard" };
        frame.LedU = new float[] { 0.5f };   // col = round(0.5 * 21) = 11 (rounding to nearest int: 10 or 11)
        frame.LedV = new float[] { 0.333f };  // row = round(0.333 * 5) = 2

        effect.WriteToDevices(new[] { frame });

        var leds = frame.LedBytes;
        var expectedRow = (int)MathF.Round(0.333f * (rows - 1));  // 2
        var expectedCol = (int)MathF.Round(0.5f * (cols - 1));    // 11

        Assert.Equal((byte)(expectedRow * 10), leds[0]); // R
        Assert.Equal((byte)(expectedCol * 10), leds[1]); // G
        Assert.Equal(0, leds[2]);                         // B
    }

    [Fact]
    public void WriteToDevices_Keyboard_8x24Grid_CustomEffectMapsCorners()
    {
        // CHROMA_CUSTOM2: 8 rows x 24 cols. Corners colored distinctly.
        const int rows = 8, cols = 24;
        var colors = new int[rows * cols];
        colors[0] = Colorref(255, 0, 0);                       // (0,0) top-left
        colors[cols - 1] = Colorref(0, 255, 0);                // (0,23) top-right
        colors[(rows - 1) * cols] = Colorref(0, 0, 255);       // (7,0) bottom-left
        colors[rows * cols - 1] = Colorref(255, 255, 0);       // (7,23) bottom-right

        var effect = new GameSyncEffect();
        effect.IngestFrame("keyboard", "CHROMA_CUSTOM2", rows, cols, colors);

        var frame = new DeviceFrame(0, "kb:keys", 4) { Archetype = "keyboard" };
        frame.LedU = new float[] { 0f, 1f, 0f, 1f };
        frame.LedV = new float[] { 0f, 0f, 1f, 1f };

        effect.WriteToDevices(new[] { frame });

        var leds = frame.LedBytes;
        Assert.Equal(255, leds[0]); Assert.Equal(0, leds[1]); Assert.Equal(0, leds[2]);   // top-left: red
        Assert.Equal(0, leds[3]); Assert.Equal(255, leds[4]); Assert.Equal(0, leds[5]);   // top-right: green
        Assert.Equal(0, leds[6]); Assert.Equal(0, leds[7]); Assert.Equal(255, leds[8]);   // bottom-left: blue
        Assert.Equal(255, leds[9]); Assert.Equal(255, leds[10]); Assert.Equal(0, leds[11]); // bottom-right: yellow
    }

    [Fact]
    public void WriteToDevices_Keyboard_DisabledLedsWriteBlack()
    {
        var colors = SolidGrid(1, 1, 200, 100, 50);
        var effect = new GameSyncEffect();
        effect.IngestFrame("keyboard", "CHROMA_CUSTOM", 1, 1, colors);

        var frame = new DeviceFrame(0, "kb:keys", 2) { Archetype = "keyboard" };
        frame.LedU = new float[] { 0f, 0f };
        frame.LedV = new float[] { 0f, 0f };
        frame.LedDisabled = new bool[] { false, true }; // LED 1 is disabled

        effect.WriteToDevices(new[] { frame });

        var leds = frame.LedBytes;
        // LED 0: gets color from grid
        Assert.Equal(200, leds[0]);
        // LED 1: disabled -> black
        Assert.Equal(0, leds[3]);
        Assert.Equal(0, leds[4]);
        Assert.Equal(0, leds[5]);
    }

    // ── KeebLayout UV round-trip ─────────────────────────────────────────────

    [Fact]
    public void WriteToDevices_KeebLayoutUvs_MapsKeyToExpectedCell()
    {
        // The HYTE Keeb TKL key matrix: wire value encodes (row, col) in a
        // stride-21 matrix. KeebLayout.ComputeKeyUv normalizes by maxCol=20,
        // maxRow=5. Verify that a known key (e.g. wire value 0 -> row=0, col=0)
        // samples cell (0,0) of a 6x22 Chroma grid.
        var (ledU, ledV) = KeebLayout.ComputeKeyUv();

        const int rows = 6, cols = 22;
        var colors = new int[rows * cols];
        // Cell (row=0, col=0) = red; everything else black.
        colors[0] = Colorref(255, 0, 0);

        var effect = new GameSyncEffect();
        effect.IngestFrame("keyboard", "CHROMA_CUSTOM", rows, cols, colors);

        // LED 0 has wire value 0 -> U = 0/20 = 0, V = 0/5 = 0 -> maps to cell (0,0).
        var frame = new DeviceFrame(0, "kb:keys", 1) { Archetype = "keyboard" };
        frame.LedU = new float[] { ledU[0] };
        frame.LedV = new float[] { ledV[0] };

        effect.WriteToDevices(new[] { frame });

        var leds = frame.LedBytes;
        Assert.Equal(255, leds[0]); // R
        Assert.Equal(0, leds[1]);   // G
        Assert.Equal(0, leds[2]);   // B
    }

    // ── Semantic peripheral routing ──────────────────────────────────────────

    [Theory]
    [InlineData("mouse", "mouse")]
    [InlineData("mousepad", "mousepad")]
    [InlineData("headset", "headset")]
    [InlineData("keypad", "keypad")]
    [InlineData("chromalink", "chromalink")]
    public void WriteToDevices_SemanticPeripheral_FillsWithDominantColor(
        string shimDevice, string archetype)
    {
        var colors = SolidGrid(1, 7, 100, 150, 200);
        var effect = new GameSyncEffect();
        effect.IngestFrame(shimDevice, "CHROMA_CUSTOM", 1, 7, colors);

        var frame = new DeviceFrame(0, "dev-1", 3) { Archetype = archetype };

        effect.WriteToDevices(new[] { frame });

        var leds = frame.LedBytes;
        Assert.Equal(100, leds[0]);
        Assert.Equal(150, leds[1]);
        Assert.Equal(200, leds[2]);
    }

    [Fact]
    public void WriteToDevices_NullArchetype_DoesNotModifyFrame()
    {
        // Devices with null Archetype are handled by SampleDevicesFromCanvas,
        // so WriteToDevices must leave their LEDs unchanged.
        var colors = SolidGrid(1, 1, 255, 0, 0);
        var effect = new GameSyncEffect();
        effect.IngestFrame("keyboard", "CHROMA_CUSTOM", 1, 1, colors);

        // Pre-seed the frame with green so we can detect if it was overwritten.
        var frame = new DeviceFrame(0, "strip-1", 2);
        frame.Fill(0, 200, 0);

        effect.WriteToDevices(new[] { frame });

        var leds = frame.LedBytes;
        Assert.Equal(0, leds[0]);   // R unchanged
        Assert.Equal(200, leds[1]); // G unchanged
        Assert.Equal(0, leds[2]);   // B unchanged
    }

    // ── CHROMA_NONE clears the device ────────────────────────────────────────

    [Fact]
    public void IngestFrame_ChromaNone_ClearsKeyboardAndMouseStaysUnchanged()
    {
        var effect = new GameSyncEffect();
        // Ingest keyboard frame then a CHROMA_NONE for keyboard.
        effect.IngestFrame("keyboard", "CHROMA_CUSTOM", 1, 1, new[] { Colorref(255, 0, 0) });
        effect.IngestFrame("mouse", "CHROMA_CUSTOM", 1, 1, new[] { Colorref(0, 0, 255) });
        effect.IngestFrame("keyboard", "CHROMA_NONE", 0, 0, Array.Empty<int>());

        var kbFrame = new DeviceFrame(0, "kb:keys", 1) { Archetype = "keyboard" };
        kbFrame.LedU = new float[] { 0f };
        kbFrame.LedV = new float[] { 0f };
        // Pre-seed with white to confirm it is not overwritten by a null grid.
        kbFrame.Fill(255, 255, 255);

        var mouseFrame = new DeviceFrame(1, "mouse-1", 1) { Archetype = "mouse" };

        effect.WriteToDevices(new[] { kbFrame, mouseFrame });

        // Keyboard: no grid, WriteToDevices skips it -> remains pre-seeded white.
        var kbLeds = kbFrame.LedBytes;
        Assert.Equal(255, kbLeds[0]);

        // Mouse: still has its grid -> filled with blue.
        var mouseLeds = mouseFrame.LedBytes;
        Assert.Equal(0, mouseLeds[0]);
        Assert.Equal(0, mouseLeds[1]);
        Assert.Equal(255, mouseLeds[2]);
    }

    // ── CHROMA_STATIC handling ───────────────────────────────────────────────

    [Fact]
    public void IngestFrame_ChromaStaticKeyboard_CanvasGetsColor()
    {
        // CHROMA_STATIC keyboard: _keyboardRgb is nulled so WriteToDevices
        // skips the keyboard frame; the canvas fallback carries the static color.
        var effect = new GameSyncEffect();
        effect.IngestFrame("keyboard", "CHROMA_STATIC", 1, 1, new[] { Colorref(100, 150, 200) });

        var canvas = new CanvasBuffer(1, 1);
        effect.RenderFrame(canvas, 0);

        var (r, g, b) = canvas.GetPixel(0, 0);
        Assert.Equal(100, r);
        Assert.Equal(150, g);
        Assert.Equal(200, b);
    }

    [Fact]
    public void IngestFrame_ChromaStaticMouse_FillsMouseFrameViaSolid()
    {
        var effect = new GameSyncEffect();
        effect.IngestFrame("mouse", "CHROMA_STATIC", 1, 1, new[] { Colorref(55, 77, 99) });

        var mouseFrame = new DeviceFrame(0, "mouse-1", 2) { Archetype = "mouse" };
        effect.WriteToDevices(new[] { mouseFrame });

        var leds = mouseFrame.LedBytes;
        Assert.Equal(55, leds[0]);
        Assert.Equal(77, leds[1]);
        Assert.Equal(99, leds[2]);
    }

    // ── No frame yet: WriteToDevices is a no-op ──────────────────────────────

    [Fact]
    public void WriteToDevices_BeforeAnyIngest_DoesNothing()
    {
        var effect = new GameSyncEffect();
        var frame = new DeviceFrame(0, "kb:keys", 1) { Archetype = "keyboard" };
        frame.LedU = new float[] { 0f };
        frame.LedV = new float[] { 0f };
        frame.Fill(10, 20, 30);

        effect.WriteToDevices(new[] { frame });

        var leds = frame.LedBytes;
        // Frame is not modified before any Ingest call.
        Assert.Equal(10, leds[0]);
        Assert.Equal(20, leds[1]);
        Assert.Equal(30, leds[2]);
    }

    // ── Dominant color averaging ─────────────────────────────────────────────

    [Fact]
    public void IngestFrame_Mouse_DominantColorAveragesNonBlackCells()
    {
        // Two non-black cells: (100,0,0) and (0,200,0). Black cell ignored.
        var colors = new[]
        {
            Colorref(100, 0, 0),
            Colorref(0, 200, 0),
            Colorref(0, 0, 0),      // black - excluded from average
        };
        var effect = new GameSyncEffect();
        effect.IngestFrame("mouse", "CHROMA_CUSTOM", 1, 3, colors);

        var frame = new DeviceFrame(0, "mouse-1", 1) { Archetype = "mouse" };
        effect.WriteToDevices(new[] { frame });

        var leds = frame.LedBytes;
        // Average of (100,0,0) and (0,200,0) = (50, 100, 0).
        Assert.Equal(50, leds[0]);
        Assert.Equal(100, leds[1]);
        Assert.Equal(0, leds[2]);
    }

    // ── DeviceFrame.Archetype assignment ────────────────────────────────────

    [Fact]
    public void DeviceFrame_DefaultArchetypeIsNull()
    {
        var frame = new DeviceFrame(0, "test", 4);
        Assert.Null(frame.Archetype);
    }

    [Fact]
    public void DeviceFrame_ArchetypeCanBeSet()
    {
        var frame = new DeviceFrame(0, "test", 4) { Archetype = "keyboard" };
        Assert.Equal("keyboard", frame.Archetype);
    }
}
