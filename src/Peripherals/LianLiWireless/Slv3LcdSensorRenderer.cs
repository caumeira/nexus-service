using System;
using System.Globalization;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Renders a 400x400 JPEG sensor gauge for the SL-LCD Wireless screen: a
/// ring/arc style (L-Connect's SENSOR_6/7 rings) and a big-number-with-bar
/// style. Pure: takes an already-resolved value/min/max/label/unit
/// (<see cref="Slv3LcdSensorReader"/> supplies the live reading).
/// </summary>
public static class Slv3LcdSensorRenderer
{
    public const int Width = Slv3LcdProtocol.PanelWidth;
    public const int Height = Slv3LcdProtocol.PanelHeight;

    private static readonly Color DefaultAccent = Color.ParseHex("#00D1FF");
    private static readonly Color DefaultText = Color.White;
    private static readonly Color TrackColor = Color.FromPixel(new Rgba32(255, 255, 255, 40));

    public static byte[] Render(string? style, float value, float min, float max, string label, string unit, string? accentHex, string? textHex)
    {
        var accent = Slv3LcdRenderKit.ParseColor(accentHex, DefaultAccent);
        var text = Slv3LcdRenderKit.ParseColor(textHex, DefaultText);
        var fraction = Normalize(value, min, max);
        var valueText = Math.Round(value).ToString("F0", CultureInfo.InvariantCulture);

        return string.Equals(style, "bar", StringComparison.OrdinalIgnoreCase)
            ? RenderBar(fraction, label, valueText, unit, accent, text)
            : RenderRing(fraction, label, valueText, unit, accent, text);
    }

    private static byte[] RenderRing(float fraction, string label, string valueText, string unit, Color accent, Color text)
    {
        using var image = new Image<Rgba32>(Width, Height);
        var center = new PointF(Width / 2f, Height / 2f);
        const float outerRadius = 170f;
        const float thickness = 26f;
        const float innerRadius = outerRadius - thickness;
        const float startDeg = 130f;
        const float sweepDeg = 280f;

        var font = Slv3LcdRenderKit.ResolveFont();

        image.Mutate(ctx =>
        {
            ctx.Fill(Color.Black);
            ctx.Fill(TrackColor, Slv3LcdRenderKit.BuildRingSegment(center, innerRadius, outerRadius, startDeg, startDeg + sweepDeg));
            if (fraction > 0f)
            {
                ctx.Fill(accent, Slv3LcdRenderKit.BuildRingSegment(center, innerRadius, outerRadius, startDeg, startDeg + sweepDeg * fraction));
            }

            Slv3LcdRenderKit.DrawCentered(ctx, valueText, font.CreateFont(72, FontStyle.Bold), text, new PointF(center.X, center.Y - 16));
            Slv3LcdRenderKit.DrawCentered(ctx, unit, font.CreateFont(26, FontStyle.Regular), text, new PointF(center.X, center.Y + 44));
            Slv3LcdRenderKit.DrawCentered(ctx, label.ToUpperInvariant(), font.CreateFont(22, FontStyle.Regular), accent, new PointF(center.X, center.Y + 120));
        });

        return Slv3LcdRenderKit.EncodeJpeg(image);
    }

    private static byte[] RenderBar(float fraction, string label, string valueText, string unit, Color accent, Color text)
    {
        using var image = new Image<Rgba32>(Width, Height);
        var font = Slv3LcdRenderKit.ResolveFont();

        const float barX = 60f;
        const float barWidth = Width - barX * 2f;
        const float barY = 260f;
        const float barHeight = 40f;
        var filledWidth = barWidth * fraction;

        image.Mutate(ctx =>
        {
            ctx.Fill(Color.Black);
            Slv3LcdRenderKit.DrawCentered(ctx, valueText, font.CreateFont(96, FontStyle.Bold), text, new PointF(Width / 2f, 150f));
            Slv3LcdRenderKit.DrawCentered(ctx, unit, font.CreateFont(28, FontStyle.Regular), text, new PointF(Width / 2f, 210f));

            var track = new RectangleF(barX, barY, barWidth, barHeight);
            ctx.Fill(TrackColor, track);
            if (filledWidth > 0f)
            {
                ctx.Fill(accent, new RectangleF(barX, barY, filledWidth, barHeight));
            }

            Slv3LcdRenderKit.DrawCentered(ctx, label.ToUpperInvariant(), font.CreateFont(22, FontStyle.Regular), accent, new PointF(Width / 2f, barY + barHeight + 34f));
        });

        return Slv3LcdRenderKit.EncodeJpeg(image);
    }

    private static float Normalize(float value, float min, float max)
    {
        if (max <= min)
        {
            return 0f;
        }
        return Math.Clamp((value - min) / (max - min), 0f, 1f);
    }
}
