using System;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Nexus.Service.Rendering;

/// <summary>
/// Renders the "hold to edit" affordance a blank Stream Deck key shows while
/// held: a pencil glyph inside a ring whose accent arc fills clockwise from
/// the top as the hold progresses (fraction 0..1). At fraction 1 the ring is
/// full and the connection worker fires the open-editor intent. Pure and
/// square (KeyPixelSize per side), returning an <see cref="Image{Rgba32}"/>
/// the worker runs through the same orient/transform/encode pipeline as a
/// monitoring tile. Uses only primitive fills (no glyph rasterization, which
/// the service cannot do) so it is AOT-clean like the rest of RenderKit.
/// </summary>
internal static class DeckHoldPromptRenderer
{
    private static readonly Color Background = Color.Black;
    private static readonly Color Track = Color.FromPixel(new Rgba32(255, 255, 255, 45));
    private static readonly Color Accent = Color.ParseHex("#4DA3FF");
    private static readonly Color Body = Color.White;

    public static Image<Rgba32> Render(float fraction, int pixelSize)
    {
        var clamped = Math.Clamp(fraction, 0f, 1f);
        var image = new Image<Rgba32>(pixelSize, pixelSize);
        var center = new PointF(pixelSize / 2f, pixelSize / 2f);
        var outerRadius = pixelSize * 0.40f;
        var thickness = pixelSize * 0.10f;
        var innerRadius = outerRadius - thickness;

        image.Mutate(ctx =>
        {
            ctx.Fill(Background);
            ctx.Fill(Track, RenderKit.BuildRingSegment(center, innerRadius, outerRadius, 0f, 360f));
            if (clamped > 0f)
            {
                // Clockwise from 12 o'clock: BuildRingSegment measures degrees
                // clockwise from the positive x-axis (screen space, y down),
                // so 12 o'clock is -90.
                ctx.Fill(Accent, RenderKit.BuildRingSegment(center, innerRadius, outerRadius, -90f, -90f + 360f * clamped));
            }
            DrawPencil(ctx, center, innerRadius);
        });

        return image;
    }

    /// <summary>
    /// A pencil on the bottom-left-to-top-right diagonal: a white shaft, a
    /// pointed tip, and an accent band at the wood/paint line. Sized to sit
    /// inside <paramref name="innerRadius"/>.
    /// </summary>
    private static void DrawPencil(IImageProcessingContext ctx, PointF center, float innerRadius)
    {
        var halfLen = innerRadius * 0.80f;
        var shaftHalf = innerRadius * 0.20f;
        var tipLen = innerRadius * 0.42f;
        var bandThickness = innerRadius * 0.12f;

        var inv = 1f / MathF.Sqrt(2f);
        var dir = new PointF(inv, -inv);
        var perp = new PointF(inv, inv);

        var tip = Add(center, dir, halfLen);
        var tipBase = Add(center, dir, halfLen - tipLen);
        var back = Add(center, dir, -halfLen);

        ctx.Fill(Body, Quad(back, tipBase, perp, shaftHalf));
        ctx.Fill(Accent, Triangle(
            Offset(tipBase, perp, shaftHalf),
            Offset(tipBase, perp, -shaftHalf),
            tip));
        var bandCenter = Add(tipBase, dir, -bandThickness / 2f);
        ctx.Fill(Accent, Quad(
            Add(bandCenter, dir, -bandThickness / 2f),
            Add(bandCenter, dir, bandThickness / 2f),
            perp, shaftHalf));
    }

    private static PointF Add(PointF origin, PointF dir, float distance) =>
        new(origin.X + dir.X * distance, origin.Y + dir.Y * distance);

    private static PointF Offset(PointF origin, PointF perp, float distance) =>
        new(origin.X + perp.X * distance, origin.Y + perp.Y * distance);

    /// <summary>Rectangle spanning a..b along its axis, widened by +/- half along perp.</summary>
    private static IPath Quad(PointF a, PointF b, PointF perp, float half) =>
        new Polygon(new LinearLineSegment(new[]
        {
            Offset(a, perp, half),
            Offset(b, perp, half),
            Offset(b, perp, -half),
            Offset(a, perp, -half),
        }));

    private static IPath Triangle(PointF a, PointF b, PointF c) =>
        new Polygon(new LinearLineSegment(new[] { a, b, c }));
}
