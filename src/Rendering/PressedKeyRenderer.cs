using System;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Nexus.Service.Rendering;

/// <summary>
/// Elgato-software push-in parity: scales a decoded key image down and
/// composites it centered onto a background-filled canvas the same size as
/// the source, so a physical key-down shows an inset variant of whatever the
/// key currently displays. Pure ImageSharp compositing - no deck, HID, or
/// wire-format knowledge, so it works on any already-decoded square image.
/// </summary>
internal static class PressedKeyRenderer
{
    /// <summary>User-tuned push-in inset amount.</summary>
    internal const float PressScale = 0.80f;

    private static readonly Color DefaultBackground = Color.ParseHex("0e1116");

    public static Image<Rgba32> Render(Image<Rgba32> source, string? backgroundColorHex)
    {
        var background = RenderKit.ParseColor(backgroundColorHex, DefaultBackground);
        var scaledWidth = Math.Max(1, (int)MathF.Round(source.Width * PressScale));
        var scaledHeight = Math.Max(1, (int)MathF.Round(source.Height * PressScale));
        var offsetX = (source.Width - scaledWidth) / 2;
        var offsetY = (source.Height - scaledHeight) / 2;

        using var scaled = source.Clone(ctx => ctx.Resize(scaledWidth, scaledHeight));
        var canvas = new Image<Rgba32>(source.Width, source.Height);
        canvas.Mutate(ctx =>
        {
            ctx.Fill(background);
            ctx.DrawImage(scaled, new Point(offsetX, offsetY), 1f);
        });
        return canvas;
    }
}
