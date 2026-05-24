using System;

namespace Nexus.Service.Lighting.Engine;

public static class HsvLut
{
    public static readonly byte[] R = new byte[256];
    public static readonly byte[] G = new byte[256];
    public static readonly byte[] B = new byte[256];

    static HsvLut()
    {
        for (int i = 0; i < 256; i++)
        {
            var h = i / 256.0;
            var hi = (int)(h * 6) % 6;
            var f = h * 6 - Math.Floor(h * 6);
            byte v = 255;
            var p = (byte)0;
            var q = (byte)(255 * (1 - f));
            var t = (byte)(255 * f);
            switch (hi)
            {
                case 0:
                    R[i] = v;
                    G[i] = t;
                    B[i] = p;
                    break;
                case 1:
                    R[i] = q;
                    G[i] = v;
                    B[i] = p;
                    break;
                case 2:
                    R[i] = p;
                    G[i] = v;
                    B[i] = t;
                    break;
                case 3:
                    R[i] = p;
                    G[i] = q;
                    B[i] = v;
                    break;
                case 4:
                    R[i] = t;
                    G[i] = p;
                    B[i] = v;
                    break;
                default:
                    R[i] = v;
                    G[i] = p;
                    B[i] = q;
                    break;
            }
        }
    }

    public static void SetPixel(CanvasBuffer canvas, int x, int y, byte hue)
    {
        canvas.SetPixel(x, y, R[hue], G[hue], B[hue]);
    }

    public static void SetPixelBright(CanvasBuffer canvas, int x, int y, byte hue, float brightness)
    {
        canvas.SetPixel(x, y, (byte)(R[hue] * brightness), (byte)(G[hue] * brightness), (byte)(B[hue] * brightness));
    }
}
