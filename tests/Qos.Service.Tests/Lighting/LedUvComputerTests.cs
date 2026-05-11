using Qos.Service.Lighting.Rgb;

namespace Qos.Service.Tests.Lighting;

/// <summary>
/// Unit tests for LedUvComputer, which assigns (u,v) positions to each LED
/// on an RGB device. Matrix-zone LEDs get grid coordinates, linear-zone LEDs
/// get clockwise perimeter positions when a matrix is present.
/// </summary>
public class LedUvComputerTests
{
    private const float Tolerance = 0.01f;

    private static void AssertApprox(float expected, float actual, string label)
    {
        Assert.True(
            Math.Abs(expected - actual) < Tolerance,
            $"{label}: expected {expected:F4} but got {actual:F4}");
    }

    // ── Empty device ─────────────────────────────────────────────────────

    [Fact]
    public void EmptyDevice_ReturnsEmptyArrays()
    {
        var device = new RgbDevice { LedCount = 0, Zones = new() };
        var (ledU, ledV) = LedUvComputer.ComputeDefaults(device);

        Assert.Empty(ledU);
        Assert.Empty(ledV);
    }

    [Fact]
    public void NegativeLedCount_ReturnsEmptyArrays()
    {
        var device = new RgbDevice { LedCount = -1, Zones = new() };
        var (ledU, ledV) = LedUvComputer.ComputeDefaults(device);

        Assert.Empty(ledU);
        Assert.Empty(ledV);
    }

    // ── Matrix-only device ───────────────────────────────────────────────

    [Fact]
    public void MatrixOnly_2x2_LedsGetGridPositions()
    {
        // 2x2 matrix, indices: [0,1,2,3]
        // row0: col0=0 col1=1  -> u=0,v=0 and u=1,v=0
        // row1: col0=2 col1=3  -> u=0,v=1 and u=1,v=1
        var device = new RgbDevice
        {
            LedCount = 4,
            Zones = new()
            {
                new RgbZone
                {
                    ZoneType = 2,
                    LedCount = 4,
                    MatrixWidth = 2,
                    MatrixHeight = 2,
                    MatrixMap = new[] { 0, 1, 2, 3 },
                },
            },
        };

        var (ledU, ledV) = LedUvComputer.ComputeDefaults(device);

        Assert.Equal(4, ledU.Length);
        Assert.Equal(4, ledV.Length);

        // LED 0: top-left (0,0)
        AssertApprox(0f, ledU[0], "LED0 u");
        AssertApprox(0f, ledV[0], "LED0 v");

        // LED 1: top-right (1,0)
        AssertApprox(1f, ledU[1], "LED1 u");
        AssertApprox(0f, ledV[1], "LED1 v");

        // LED 2: bottom-left (0,1)
        AssertApprox(0f, ledU[2], "LED2 u");
        AssertApprox(1f, ledV[2], "LED2 v");

        // LED 3: bottom-right (1,1)
        AssertApprox(1f, ledU[3], "LED3 u");
        AssertApprox(1f, ledV[3], "LED3 v");
    }

    [Fact]
    public void MatrixOnly_3x2_MiddleColumnGetsHalfU()
    {
        // 3 columns, 2 rows. 6 LEDs.
        // row0: [0,1,2] -> u=0,0.5,1 v=0
        // row1: [3,4,5] -> u=0,0.5,1 v=1
        var device = new RgbDevice
        {
            LedCount = 6,
            Zones = new()
            {
                new RgbZone
                {
                    ZoneType = 2,
                    LedCount = 6,
                    MatrixWidth = 3,
                    MatrixHeight = 2,
                    MatrixMap = new[] { 0, 1, 2, 3, 4, 5 },
                },
            },
        };

        var (ledU, ledV) = LedUvComputer.ComputeDefaults(device);

        // LED 1: top-center
        AssertApprox(0.5f, ledU[1], "LED1 u");
        AssertApprox(0f, ledV[1], "LED1 v");

        // LED 4: bottom-center
        AssertApprox(0.5f, ledU[4], "LED4 u");
        AssertApprox(1f, ledV[4], "LED4 v");
    }

    // ── Matrix + linear (perimeter distribution) ─────────────────────────

    [Fact]
    public void MatrixPlusLinear_LinearLedsGetPerimeterPositions()
    {
        // 4 matrix LEDs (2x2) + 4 linear LEDs = 8 total.
        // The 4 linear LEDs (indices 4-7) should be distributed around the perimeter.
        var device = new RgbDevice
        {
            LedCount = 8,
            Zones = new()
            {
                new RgbZone
                {
                    ZoneType = 2,
                    LedCount = 4,
                    MatrixWidth = 2,
                    MatrixHeight = 2,
                    MatrixMap = new[] { 0, 1, 2, 3 },
                },
                new RgbZone
                {
                    ZoneType = 1, // Linear
                    LedCount = 4,
                    MatrixWidth = 0,
                    MatrixHeight = 0,
                    MatrixMap = null,
                },
            },
        };

        var (ledU, ledV) = LedUvComputer.ComputeDefaults(device);

        Assert.Equal(8, ledU.Length);
        Assert.Equal(8, ledV.Length);

        // Matrix LEDs (0-3) get grid positions
        AssertApprox(0f, ledU[0], "Matrix LED0 u");
        AssertApprox(0f, ledV[0], "Matrix LED0 v");

        // Linear LEDs (4-7) get perimeter positions - should be non-default
        // At least verify they got assigned reasonable values in [0,1]
        for (int i = 4; i < 8; i++)
        {
            Assert.InRange(ledU[i], 0f, 1f);
            Assert.InRange(ledV[i], 0f, 1f);
        }
    }

    // ── Perimeter clockwise ordering ─────────────────────────────────────

    [Fact]
    public void Perimeter_StartsTopLeft_GoesClockwise()
    {
        // 1 matrix LED (1x1) + 5 linear LEDs = 6 total.
        // The 5 linear LEDs walk the perimeter from (0,0) clockwise.
        // With n=5, t steps: 0, 1, 2, 3, 4 mapped to t*4/(n-1) = t
        // t=0 -> top-left (0,0)
        // t=1 -> top-right (1,0) - end of top edge
        // t=2 -> bottom-right (1,1) - end of right edge
        // t=3 -> bottom-left (0,1) - end of bottom edge
        // t=4 -> back to top-left (0,0) - end of left edge
        var device = new RgbDevice
        {
            LedCount = 6,
            Zones = new()
            {
                new RgbZone
                {
                    ZoneType = 2,
                    LedCount = 1,
                    MatrixWidth = 1,
                    MatrixHeight = 1,
                    MatrixMap = new[] { 0 },
                },
                new RgbZone
                {
                    ZoneType = 1,
                    LedCount = 5,
                    MatrixWidth = 0,
                    MatrixHeight = 0,
                    MatrixMap = null,
                },
            },
        };

        var (ledU, ledV) = LedUvComputer.ComputeDefaults(device);

        // LED 1 (first perimeter): top-left (0,0)
        AssertApprox(0f, ledU[1], "Perim0 u");
        AssertApprox(0f, ledV[1], "Perim0 v");

        // LED 2: top-right (1,0)
        AssertApprox(1f, ledU[2], "Perim1 u");
        AssertApprox(0f, ledV[2], "Perim1 v");

        // LED 3: bottom-right (1,1)
        AssertApprox(1f, ledU[3], "Perim2 u");
        AssertApprox(1f, ledV[3], "Perim2 v");

        // LED 4: bottom-left (0,1)
        AssertApprox(0f, ledU[4], "Perim3 u");
        AssertApprox(1f, ledV[4], "Perim3 v");

        // LED 5: back to top-left (0,0)
        AssertApprox(0f, ledU[5], "Perim4 u");
        AssertApprox(0f, ledV[5], "Perim4 v");
    }

    [Fact]
    public void Perimeter_MidpointEdge_InterpolatesCorrectly()
    {
        // 1 matrix LED (1x1) + 3 linear LEDs = 4 total.
        // With n=3, t steps: 0, 2, 4 -> t = i/(n-1)*4 = i*2
        // t=0: top-left (0,0)
        // t=2: bottom-right (1,1) - end of right edge
        // t=4: top-left (0,0) - end of left edge
        var device = new RgbDevice
        {
            LedCount = 4,
            Zones = new()
            {
                new RgbZone
                {
                    ZoneType = 2,
                    LedCount = 1,
                    MatrixWidth = 1,
                    MatrixHeight = 1,
                    MatrixMap = new[] { 0 },
                },
                new RgbZone
                {
                    ZoneType = 1,
                    LedCount = 3,
                    MatrixWidth = 0,
                    MatrixHeight = 0,
                    MatrixMap = null,
                },
            },
        };

        var (ledU, ledV) = LedUvComputer.ComputeDefaults(device);

        // LED 1: t=0 -> (0,0)
        AssertApprox(0f, ledU[1], "Perim0 u");
        AssertApprox(0f, ledV[1], "Perim0 v");

        // LED 2: t=2 -> (1,1) - end of right edge
        AssertApprox(1f, ledU[2], "Perim1 u");
        AssertApprox(1f, ledV[2], "Perim1 v");

        // LED 3: t=4 -> (0,0) - end of left edge, back to start
        AssertApprox(0f, ledU[3], "Perim2 u");
        AssertApprox(0f, ledV[3], "Perim2 v");
    }

    // ── Linear-only (no matrix) ──────────────────────────────────────────

    [Fact]
    public void LinearOnly_NoMatrix_ReturnsEmptyArrays()
    {
        var device = new RgbDevice
        {
            LedCount = 10,
            Zones = new()
            {
                new RgbZone
                {
                    ZoneType = 1,
                    LedCount = 10,
                    MatrixWidth = 0,
                    MatrixHeight = 0,
                    MatrixMap = null,
                },
            },
        };

        var (ledU, ledV) = LedUvComputer.ComputeDefaults(device);

        Assert.Empty(ledU);
        Assert.Empty(ledV);
    }

    [Fact]
    public void SingleZoneOnly_NoMatrix_ReturnsEmptyArrays()
    {
        var device = new RgbDevice
        {
            LedCount = 1,
            Zones = new()
            {
                new RgbZone
                {
                    ZoneType = 0, // Single
                    LedCount = 1,
                    MatrixWidth = 0,
                    MatrixHeight = 0,
                    MatrixMap = null,
                },
            },
        };

        var (ledU, ledV) = LedUvComputer.ComputeDefaults(device);

        Assert.Empty(ledU);
        Assert.Empty(ledV);
    }

    // ── Matrix with gaps (-1 in map) ─────────────────────────────────────

    [Fact]
    public void MatrixWithGaps_GapLedsGetFallbackPositions()
    {
        // 2x3 matrix with 4 LEDs but 2 gaps (-1).
        // Map:
        //   row0: [0, -1, 1]
        //   row1: [-1, 2, 3]
        // LED 0: col0,row0 -> u=0,   v=0
        // LED 1: col2,row0 -> u=1,   v=0
        // LED 2: col1,row1 -> u=0.5, v=1
        // LED 3: col2,row1 -> u=1,   v=1
        // No linear zones, so DistributeLinearFallback is called.
        // All 4 LEDs get matrix positions, so no fallback needed.
        var device = new RgbDevice
        {
            LedCount = 4,
            Zones = new()
            {
                new RgbZone
                {
                    ZoneType = 2,
                    LedCount = 4,
                    MatrixWidth = 3,
                    MatrixHeight = 2,
                    MatrixMap = new[] { 0, -1, 1, -1, 2, 3 },
                },
            },
        };

        var (ledU, ledV) = LedUvComputer.ComputeDefaults(device);

        Assert.Equal(4, ledU.Length);

        // LED 0: row0, col0
        AssertApprox(0f, ledU[0], "LED0 u");
        AssertApprox(0f, ledV[0], "LED0 v");

        // LED 1: row0, col2
        AssertApprox(1f, ledU[1], "LED1 u");
        AssertApprox(0f, ledV[1], "LED1 v");

        // LED 2: row1, col1
        AssertApprox(0.5f, ledU[2], "LED2 u");
        AssertApprox(1f, ledV[2], "LED2 v");

        // LED 3: row1, col2
        AssertApprox(1f, ledU[3], "LED3 u");
        AssertApprox(1f, ledV[3], "LED3 v");
    }

    [Fact]
    public void MatrixWithGaps_UnmappedLedsGetLinearFallback()
    {
        // 2x2 matrix with 6 LEDs but only 4 in the map, 2 LEDs never appear.
        // LED indices 4 and 5 are never referenced in the map, so they stay
        // unpopulated and DistributeLinearFallback fills them at v=0.5.
        var device = new RgbDevice
        {
            LedCount = 6,
            Zones = new()
            {
                new RgbZone
                {
                    ZoneType = 2,
                    LedCount = 6,
                    MatrixWidth = 2,
                    MatrixHeight = 2,
                    MatrixMap = new[] { 0, 1, 2, 3 },
                },
            },
        };

        var (ledU, ledV) = LedUvComputer.ComputeDefaults(device);

        Assert.Equal(6, ledU.Length);

        // LEDs 0-3 are in the matrix
        AssertApprox(0f, ledU[0], "LED0 u");
        AssertApprox(0f, ledV[0], "LED0 v");

        // LEDs 4-5 fall back to linear distribution at v=0.5
        AssertApprox(0f, ledU[4], "LED4 u (fallback first)");
        AssertApprox(0.5f, ledV[4], "LED4 v (fallback)");
        AssertApprox(1f, ledU[5], "LED5 u (fallback last)");
        AssertApprox(0.5f, ledV[5], "LED5 v (fallback)");
    }

    // ── Single-LED device (no useful matrix) ─────────────────────────────

    [Fact]
    public void SingleLedLinearDevice_ReturnsEmpty()
    {
        // A device with 1 LED in a linear zone - no matrix, returns empty.
        var device = new RgbDevice
        {
            LedCount = 1,
            Zones = new()
            {
                new RgbZone
                {
                    ZoneType = 1,
                    LedCount = 1,
                    MatrixWidth = 0,
                    MatrixHeight = 0,
                    MatrixMap = null,
                },
            },
        };

        var (ledU, ledV) = LedUvComputer.ComputeDefaults(device);

        Assert.Empty(ledU);
        Assert.Empty(ledV);
    }

    // ── Edge cases ───────────────────────────────────────────────────────

    [Fact]
    public void SinglePerimeterLed_GetsOriginPosition()
    {
        // 1 matrix LED + 1 linear LED = 2 total.
        // The single unpopulated LED: n=1, t=0 -> (0,0).
        var device = new RgbDevice
        {
            LedCount = 2,
            Zones = new()
            {
                new RgbZone
                {
                    ZoneType = 2,
                    LedCount = 1,
                    MatrixWidth = 1,
                    MatrixHeight = 1,
                    MatrixMap = new[] { 0 },
                },
                new RgbZone
                {
                    ZoneType = 1,
                    LedCount = 1,
                    MatrixWidth = 0,
                    MatrixHeight = 0,
                    MatrixMap = null,
                },
            },
        };

        var (ledU, ledV) = LedUvComputer.ComputeDefaults(device);

        Assert.Equal(2, ledU.Length);

        // LED 0: matrix 1x1 -> (0.5, 0.5)
        AssertApprox(0.5f, ledU[0], "Matrix LED u");
        AssertApprox(0.5f, ledV[0], "Matrix LED v");

        // LED 1: single perimeter LED -> (0,0)
        AssertApprox(0f, ledU[1], "Perim LED u");
        AssertApprox(0f, ledV[1], "Perim LED v");
    }

    [Fact]
    public void MultipleZones_OffsetsAccumulateCorrectly()
    {
        // Two matrix zones: first 2x1 (2 LEDs), then 1x2 (2 LEDs) = 4 total.
        // Zone 0: offset=0, map [0,1], 2 cols, 1 row -> u=0,1 v=0.5,0.5
        // Zone 1: offset=2, map [0,1], 1 col, 2 rows -> u=0.5,0.5 v=0,1
        var device = new RgbDevice
        {
            LedCount = 4,
            Zones = new()
            {
                new RgbZone
                {
                    ZoneType = 2,
                    LedCount = 2,
                    MatrixWidth = 2,
                    MatrixHeight = 1,
                    MatrixMap = new[] { 0, 1 },
                },
                new RgbZone
                {
                    ZoneType = 2,
                    LedCount = 2,
                    MatrixWidth = 1,
                    MatrixHeight = 2,
                    MatrixMap = new[] { 0, 1 },
                },
            },
        };

        var (ledU, ledV) = LedUvComputer.ComputeDefaults(device);

        Assert.Equal(4, ledU.Length);

        // Zone 0, LED 0: col0 of 2 -> u=0, row0 of 1 -> v=0.5
        AssertApprox(0f, ledU[0], "Z0 LED0 u");
        AssertApprox(0.5f, ledV[0], "Z0 LED0 v");

        // Zone 0, LED 1: col1 of 2 -> u=1, row0 of 1 -> v=0.5
        AssertApprox(1f, ledU[1], "Z0 LED1 u");
        AssertApprox(0.5f, ledV[1], "Z0 LED1 v");

        // Zone 1, LED 0 (global 2): col0 of 1 -> u=0.5, row0 of 2 -> v=0
        AssertApprox(0.5f, ledU[2], "Z1 LED0 u");
        AssertApprox(0f, ledV[2], "Z1 LED0 v");

        // Zone 1, LED 1 (global 3): col0 of 1 -> u=0.5, row1 of 2 -> v=1
        AssertApprox(0.5f, ledU[3], "Z1 LED1 u");
        AssertApprox(1f, ledV[3], "Z1 LED1 v");
    }

    [Fact]
    public void ZoneWithZeroLedCount_IsSkipped()
    {
        // A zone with LedCount=0 should be skipped without affecting offsets.
        var device = new RgbDevice
        {
            LedCount = 2,
            Zones = new()
            {
                new RgbZone
                {
                    ZoneType = 2,
                    LedCount = 0,
                    MatrixWidth = 0,
                    MatrixHeight = 0,
                    MatrixMap = null,
                },
                new RgbZone
                {
                    ZoneType = 2,
                    LedCount = 2,
                    MatrixWidth = 2,
                    MatrixHeight = 1,
                    MatrixMap = new[] { 0, 1 },
                },
            },
        };

        var (ledU, ledV) = LedUvComputer.ComputeDefaults(device);

        Assert.Equal(2, ledU.Length);

        // Zone offset should still be 0 because the first zone was skipped.
        AssertApprox(0f, ledU[0], "LED0 u");
        AssertApprox(0.5f, ledV[0], "LED0 v");
        AssertApprox(1f, ledU[1], "LED1 u");
        AssertApprox(0.5f, ledV[1], "LED1 v");
    }

    [Fact]
    public void SingleFallbackLed_GetsCenteredPosition()
    {
        // Matrix-only device with 1 extra unpopulated LED.
        // When a single LED needs fallback: unpopCount=1, so u=0.5, v=0.5.
        var device = new RgbDevice
        {
            LedCount = 3,
            Zones = new()
            {
                new RgbZone
                {
                    ZoneType = 2,
                    LedCount = 3,
                    MatrixWidth = 2,
                    MatrixHeight = 1,
                    MatrixMap = new[] { 0, 1 },
                },
            },
        };

        var (ledU, ledV) = LedUvComputer.ComputeDefaults(device);

        Assert.Equal(3, ledU.Length);

        // LED 2: only unpopulated LED, fallback -> u=0.5, v=0.5
        AssertApprox(0.5f, ledU[2], "Fallback LED u");
        AssertApprox(0.5f, ledV[2], "Fallback LED v");
    }
}
