using System;
using Nexus.Service.Lighting.Smart;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Renders a 400x400 JPEG procedural animation frame for the SL-LCD Wireless
/// screen. Three tasteful, cheap-to-compute animations; bundled-asset
/// animations (L-Connect's CefSharp index.html library) are out of scope.
/// Pure: takes the elapsed time, so a frame's content is deterministic.
/// </summary>
public static class Slv3LcdAnimationRenderer
{
    public const int Width = Slv3LcdProtocol.PanelWidth;
    public const int Height = Slv3LcdProtocol.PanelHeight;

    /// <summary>~15 fps, matching the fixed rate video import already resamples GIF/video content to.</summary>
    public const int FrameIntervalMs = 66;

    private static readonly Color DefaultColorA = Color.ParseHex("#00D1FF");
    private static readonly Color DefaultColorB = Color.ParseHex("#9B5DE5");

    public static byte[] Render(string? animationId, double elapsedSeconds, string? colorAHex, string? colorBHex)
    {
        var colorA = Slv3LcdRenderKit.ParseColor(colorAHex, DefaultColorA);
        var colorB = Slv3LcdRenderKit.ParseColor(colorBHex, DefaultColorB);

        return animationId switch
        {
            "spectrum" => RenderSpectrum(elapsedSeconds),
            "spin" => RenderSpin(elapsedSeconds, colorA, colorB),
            // "pulse" and any unrecognized/missing id default to the pulse animation.
            _ => RenderPulse(elapsedSeconds, colorA, colorB),
        };
    }

    private static byte[] RenderPulse(double elapsedSeconds, Color colorA, Color colorB)
    {
        using var image = new Image<Rgba32>(Width, Height);
        var center = new PointF(Width / 2f, Height / 2f);
        // Reduce modulo the 2-second period in double before the float cast:
        // an uptime of days/weeks otherwise loses enough float precision in
        // the raw elapsed count that the phase visibly jumps between frames.
        var cycleSeconds = (float)(elapsedSeconds % 2.0);
        var phase = (MathF.Sin(cycleSeconds * MathF.PI) + 1f) / 2f;
        var radius = 60f + phase * 110f;

        image.Mutate(ctx =>
        {
            ctx.Fill(Color.Black);
            ctx.Fill(WithAlpha(colorB, 0.25f), Slv3LcdRenderKit.BuildCircle(center, radius + 50f));
            ctx.Fill(WithAlpha(colorA, 0.55f), Slv3LcdRenderKit.BuildCircle(center, radius + 22f));
            ctx.Fill(colorA, Slv3LcdRenderKit.BuildCircle(center, radius));
        });

        return Slv3LcdRenderKit.EncodeJpeg(image);
    }

    private static byte[] RenderSpectrum(double elapsedSeconds)
    {
        using var image = new Image<Rgba32>(Width, Height);
        var hueOffset = (float)(elapsedSeconds / 4.0 % 1.0);

        image.Mutate(ctx =>
        {
            const int bandCount = 40;
            var bandWidth = (float)Width / bandCount;
            for (var i = 0; i < bandCount; i++)
            {
                var hue = ((float)i / bandCount + hueOffset) % 1f;
                var (r, g, b) = ColorMath.HsvToRgb(hue, 1f, 1f);
                ctx.Fill(Color.FromPixel(new Rgba32(r, g, b)), new RectangleF(i * bandWidth, 0f, bandWidth + 1f, Height));
            }
        });

        return Slv3LcdRenderKit.EncodeJpeg(image);
    }

    private static byte[] RenderSpin(double elapsedSeconds, Color colorA, Color colorB)
    {
        using var image = new Image<Rgba32>(Width, Height);
        var center = new PointF(Width / 2f, Height / 2f);
        const int spokeCount = 12;
        var rotation = (float)(elapsedSeconds * 120.0 % 360.0);

        image.Mutate(ctx =>
        {
            ctx.Fill(Color.Black);
            for (var i = 0; i < spokeCount; i++)
            {
                var angle = rotation + i * (360f / spokeCount);
                var fade = 1f - (float)i / spokeCount;
                var spoke = Slv3LcdRenderKit.BuildHand(center, 160f, angle, 16f, tailFraction: 0f);
                ctx.Fill(WithAlpha(colorA, 0.15f + fade * 0.85f), spoke);
            }
            ctx.Fill(colorB, Slv3LcdRenderKit.BuildCircle(center, 20f));
        });

        return Slv3LcdRenderKit.EncodeJpeg(image);
    }

    private static Color WithAlpha(Color color, float alpha)
    {
        var pixel = color.ToPixel<Rgba32>();
        var a = (byte)Math.Round(Math.Clamp(alpha, 0f, 1f) * 255f);
        return Color.FromPixel(new Rgba32(pixel.R, pixel.G, pixel.B, a));
    }
}
