using System;
using System.IO;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Shared ImageSharp drawing primitives for the SL-LCD Wireless sensor/clock/
/// animation content renderers: hex color parsing, a manual arc/ring polygon
/// builder (avoids depending on a specific PathBuilder.AddArc overload),
/// centered text, and JPEG encode. No IO beyond the in-memory encode buffer.
/// </summary>
internal static class Slv3LcdRenderKit
{
    private const int JpegQuality = 85;

    private static readonly string[] PreferredFontFamilies =
    {
        "Segoe UI", "Arial", "Helvetica Neue", "Helvetica", "DejaVu Sans", "Liberation Sans", "Verdana", "Tahoma",
    };

    private static FontFamily? _fontFamily;

    /// <summary>Resolves a cross-platform display font once and caches it for the process lifetime.</summary>
    public static FontFamily ResolveFont()
    {
        if (_fontFamily is { } cached)
        {
            return cached;
        }

        foreach (var name in PreferredFontFamilies)
        {
            if (SystemFonts.TryGet(name, out var family))
            {
                _fontFamily = family;
                return family;
            }
        }

        foreach (var family in SystemFonts.Collection.Families)
        {
            _fontFamily = family;
            return family;
        }

        throw new InvalidOperationException("No system font families available for LCD rendering.");
    }

    public static Color ParseColor(string? hex, Color fallback) =>
        !string.IsNullOrWhiteSpace(hex) && Color.TryParseHex(hex, out var parsed) ? parsed : fallback;

    /// <summary>
    /// Builds a filled ring segment (an annulus wedge) as a polygon: N points
    /// along the outer radius from startDeg to endDeg, then N points back
    /// along the inner radius. innerRadius = 0 degenerates to a filled pie
    /// slice / full disc. Angles are clockwise from the positive x-axis.
    /// </summary>
    public static IPath BuildRingSegment(PointF center, float innerRadius, float outerRadius, float startDeg, float endDeg, int segments = 96)
    {
        var points = new PointF[segments * 2 + 2];
        var idx = 0;
        for (var i = 0; i <= segments; i++)
        {
            var deg = startDeg + (endDeg - startDeg) * i / segments;
            var rad = deg * MathF.PI / 180f;
            points[idx++] = new PointF(center.X + outerRadius * MathF.Cos(rad), center.Y + outerRadius * MathF.Sin(rad));
        }
        for (var i = segments; i >= 0; i--)
        {
            var deg = startDeg + (endDeg - startDeg) * i / segments;
            var rad = deg * MathF.PI / 180f;
            points[idx++] = new PointF(center.X + innerRadius * MathF.Cos(rad), center.Y + innerRadius * MathF.Sin(rad));
        }
        return new Polygon(new LinearLineSegment(points));
    }

    public static IPath BuildCircle(PointF center, float radius, int segments = 96) =>
        BuildRingSegment(center, 0f, radius, 0f, 360f, segments);

    /// <summary>
    /// Builds a short radial tick between innerRadius and outerRadius, in the
    /// same clockwise-from-12-o'clock convention as <see cref="BuildHand"/>.
    /// </summary>
    public static IPath BuildClockTick(PointF center, float innerRadius, float outerRadius, float clockDeg, float angularWidthDeg, int segments = 4)
    {
        var start = clockDeg - 90f - angularWidthDeg / 2f;
        var end = clockDeg - 90f + angularWidthDeg / 2f;
        return BuildRingSegment(center, innerRadius, outerRadius, start, end, segments);
    }

    /// <summary>
    /// Builds a clock hand as a thin quadrilateral from a short tail behind
    /// the center out to the tip. clockDeg is clockwise from 12 o'clock (the
    /// convention hour/minute/second angles are computed in), converted here
    /// to the standard screen-space angle BuildRingSegment/trig uses.
    /// </summary>
    public static IPath BuildHand(PointF center, float length, float clockDeg, float width, float tailFraction = 0.12f)
    {
        var rad = (clockDeg - 90f) * MathF.PI / 180f;
        var dir = new PointF(MathF.Cos(rad), MathF.Sin(rad));
        var perp = new PointF(-dir.Y, dir.X);
        var tip = new PointF(center.X + dir.X * length, center.Y + dir.Y * length);
        var tail = length * tailFraction;
        var back = new PointF(center.X - dir.X * tail, center.Y - dir.Y * tail);
        var half = width / 2f;
        return new Polygon(new LinearLineSegment(new[]
        {
            new PointF(back.X + perp.X * half, back.Y + perp.Y * half),
            new PointF(tip.X + perp.X * half, tip.Y + perp.Y * half),
            new PointF(tip.X - perp.X * half, tip.Y - perp.Y * half),
            new PointF(back.X - perp.X * half, back.Y - perp.Y * half),
        }));
    }

    public static void DrawCentered(IImageProcessingContext ctx, string text, Font font, Color color, PointF center)
    {
        var size = TextMeasurer.MeasureSize(text, new TextOptions(font));
        var origin = new PointF(center.X - size.Width / 2f, center.Y - size.Height / 2f);
        ctx.DrawText(text, font, color, origin);
    }

    public static byte[] EncodeJpeg(Image<Rgba32> image)
    {
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new JpegEncoder { Quality = JpegQuality });
        return ms.ToArray();
    }
}
