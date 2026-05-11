using System;
using System.Collections.Generic;

namespace Qos.Service.Lighting.Rgb;

public static class LedUvComputer
{
    public static (float[] ledU, float[] ledV) ComputeDefaults(RgbDevice device)
    {
        if (device.LedCount <= 0)
        {
            return (Array.Empty<float>(), Array.Empty<float>());
        }

        var ledU = new float[device.LedCount];
        var ledV = new float[device.LedCount];
        var populated = new bool[device.LedCount];

        var hasMatrix = false;
        var hasLinear = false;
        foreach (var z in device.Zones)
        {
            if (z.MatrixMap is not null && z.MatrixWidth > 0 && z.MatrixHeight > 0)
            { hasMatrix = true; }
            if (z.ZoneType == 1)
            { hasLinear = true; }
        }

        if (!hasMatrix)
        {
            return (Array.Empty<float>(), Array.Empty<float>());
        }

        int zoneOffset = 0;
        foreach (var z in device.Zones)
        {
            if (z.LedCount <= 0)
            {
                continue;
            }
            if (z.MatrixMap is not null && z.MatrixWidth > 0 && z.MatrixHeight > 0)
            {
                var mw = z.MatrixWidth;
                var mh = z.MatrixHeight;
                var map = z.MatrixMap;
                for (int row = 0; row < mh; row++)
                {
                    for (int col = 0; col < mw; col++)
                    {
                        var localIdx = map[row * mw + col];
                        if (localIdx < 0 || localIdx >= z.LedCount)
                        {
                            continue;
                        }
                        var global = zoneOffset + localIdx;
                        if (global < 0 || global >= device.LedCount)
                        {
                            continue;
                        }
                        ledU[global] = mw > 1 ? col / (float)(mw - 1) : 0.5f;
                        ledV[global] = mh > 1 ? row / (float)(mh - 1) : 0.5f;
                        populated[global] = true;
                    }
                }
            }
            zoneOffset += z.LedCount;
        }

        if (hasLinear && hasMatrix)
        {
            DistributePerimeter(ledU, ledV, populated, device);
        }
        else
        {
            DistributeLinearFallback(ledU, ledV, populated, device.LedCount);
        }

        return (ledU, ledV);
    }

    private static void DistributePerimeter(float[] ledU, float[] ledV, bool[] populated, RgbDevice device)
    {
        var unpopulated = new List<int>();
        for (int i = 0; i < device.LedCount; i++)
        {
            if (!populated[i])
            {
                unpopulated.Add(i);
            }
        }

        if (unpopulated.Count == 0)
        {
            return;
        }

        // Walk clockwise from top-left: top edge, right edge, bottom edge, left edge.
        // Perimeter length = 4.0 in unit square.
        var n = unpopulated.Count;
        for (int i = 0; i < n; i++)
        {
            var t = n > 1 ? i / (float)(n - 1) * 4f : 0f;
            float u, v;
            if (t <= 1f)
            {
                // Top edge: (0,0) -> (1,0)
                u = t;
                v = 0f;
            }
            else if (t <= 2f)
            {
                // Right edge: (1,0) -> (1,1)
                u = 1f;
                v = t - 1f;
            }
            else if (t <= 3f)
            {
                // Bottom edge: (1,1) -> (0,1)
                u = 1f - (t - 2f);
                v = 1f;
            }
            else
            {
                // Left edge: (0,1) -> (0,0)
                u = 0f;
                v = 1f - (t - 3f);
            }
            var idx = unpopulated[i];
            ledU[idx] = u;
            ledV[idx] = v;
        }
    }

    private static void DistributeLinearFallback(float[] ledU, float[] ledV, bool[] populated, int ledCount)
    {
        var unpopCount = 0;
        for (int i = 0; i < ledCount; i++)
        {
            if (!populated[i])
            {
                unpopCount++;
            }
        }
        if (unpopCount == 0)
        {
            return;
        }

        var idx = 0;
        for (int i = 0; i < ledCount; i++)
        {
            if (populated[i])
            {
                continue;
            }
            ledU[i] = unpopCount > 1 ? idx / (float)(unpopCount - 1) : 0.5f;
            ledV[i] = 0.5f;
            idx++;
        }
    }
}
