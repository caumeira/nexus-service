using System;

namespace Nexus.Service.Lighting.Smart;

/// <summary>
/// Color conversions shared by the smart-light drivers. HSV (the model the
/// lighting page edits) → RGB, and RGB → CIE xy (the color space the Hue
/// CLIP API takes). Gamut-agnostic: we emit xy in the wide-RGB space Philips
/// publishes for app developers; the bridge clamps to each light's gamut.
/// </summary>
public static class ColorMath
{
    /// <summary>HSV with h,s,v in [0,1] → 8-bit sRGB.</summary>
    public static (byte r, byte g, byte b) HsvToRgb(float h, float s, float v)
    {
        h = ((h % 1f) + 1f) % 1f;
        s = Math.Clamp(s, 0f, 1f);
        v = Math.Clamp(v, 0f, 1f);
        if (s <= 0f)
        {
            var g = (byte)Math.Round(v * 255f);
            return (g, g, g);
        }
        var hh = h * 6f;
        var i = (int)Math.Floor(hh);
        var f = hh - i;
        var p = v * (1f - s);
        var q = v * (1f - s * f);
        var t = v * (1f - s * (1f - f));
        float r, gr, b;
        switch (i % 6)
        {
            case 0: r = v; gr = t; b = p; break;
            case 1: r = q; gr = v; b = p; break;
            case 2: r = p; gr = v; b = t; break;
            case 3: r = p; gr = q; b = v; break;
            case 4: r = t; gr = p; b = v; break;
            default: r = v; gr = p; b = q; break;
        }
        return ((byte)Math.Round(r * 255f), (byte)Math.Round(gr * 255f), (byte)Math.Round(b * 255f));
    }

    /// <summary>8-bit sRGB → CIE xy (Philips' published wide-RGB → XYZ matrix
    /// with sRGB gamma expansion). Returns (0,0) for black.</summary>
    public static (double x, double y) RgbToXy(byte r, byte g, byte b)
    {
        double R = r / 255.0, G = g / 255.0, B = b / 255.0;
        double rl = Expand(R), gl = Expand(G), bl = Expand(B);
        double X = rl * 0.649926 + gl * 0.103455 + bl * 0.197109;
        double Y = rl * 0.234327 + gl * 0.743075 + bl * 0.022598;
        double Z = rl * 0.0 + gl * 0.053077 + bl * 1.035763;
        double sum = X + Y + Z;
        if (sum <= 1e-9) return (0.0, 0.0);
        return (X / sum, Y / sum);

        static double Expand(double c) => c > 0.04045 ? Math.Pow((c + 0.055) / 1.055, 2.4) : c / 12.92;
    }

    /// <summary>Perceptual value (max channel) in [0,1] — used to drive a light's
    /// dimming from the streamed pixel so dark frames dim the lamp.</summary>
    public static float Value(byte r, byte g, byte b) => Math.Max(r, Math.Max(g, b)) / 255f;

    /// <summary>Kelvin → reciprocal mega-kelvin (mirek), clamped to Hue's 153..500.</summary>
    public static int KelvinToMirek(int kelvin)
        => kelvin <= 0 ? 366 : Math.Clamp((int)Math.Round(1_000_000.0 / kelvin), 153, 500);
}
