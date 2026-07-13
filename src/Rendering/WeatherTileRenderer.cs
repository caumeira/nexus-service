using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Nexus.Service.Rendering;

/// <summary>
/// Everything <see cref="WeatherTileRenderer.Render"/> needs to draw one
/// weather tile. TemperatureText and LocationLabel already carry the
/// caller's unit conversion and localization (never reformatted here),
/// mirroring MonitoringTileInput's ValueText convention.
/// </summary>
public sealed class WeatherTileInput
{
    public string TemperatureText { get; init; } = "";
    public string LocationLabel { get; init; } = "";
    /// <summary>Open-Meteo WMO weather code, selects the condition glyph. -1 when unknown.</summary>
    public int WeatherCode { get; init; } = -1;
    public string? AccentColorHex { get; init; }
    public string? BackgroundColorHex { get; init; }
    public string? TitleColorHex { get; init; }
}

/// <summary>
/// Draws a weather deck tile (condition glyph / temperature / location) into
/// a square ImageSharp image at any pixel size. Pure: no deck, HID, or
/// persistence knowledge, matching MonitoringTileRenderer's shape so both
/// live tiles share the same StreamDeckConnectionWorker render/push path.
/// The condition glyph is a simple geometric best-effort shape, not an icon
/// font - the temperature and location text are the load-bearing content.
/// </summary>
internal static class WeatherTileRenderer
{
    private static readonly Color DefaultBackground = Color.ParseHex("0e1116");
    private static readonly Color DefaultAccent = Color.ParseHex("4da3ff");
    private static readonly Color DefaultTitleColor = Color.White;

    private const float GlyphCenterYFraction = 0.24f;
    private const float GlyphRadiusFraction = 0.15f;
    private const float TemperatureCenterYFraction = 0.58f;
    private const float TemperatureFontSizeFraction = 0.26f;
    private const float LocationCenterYFraction = 0.86f;
    private const float LocationFontSizeFraction = 0.11f;

    public static Image<Rgba32> Render(WeatherTileInput input, int pixelSize)
    {
        var image = new Image<Rgba32>(pixelSize, pixelSize);
        var background = RenderKit.ParseColor(input.BackgroundColorHex, DefaultBackground);
        var accent = RenderKit.ParseColor(input.AccentColorHex, DefaultAccent);
        var titleColor = RenderKit.ParseColor(input.TitleColorHex, DefaultTitleColor);
        var font = RenderKit.ResolveFont();

        image.Mutate(ctx =>
        {
            ctx.Fill(background);
            DrawConditionGlyph(ctx, input.WeatherCode, pixelSize, accent);

            if (input.TemperatureText.Length > 0)
            {
                var tempFont = font.CreateFont(pixelSize * TemperatureFontSizeFraction, FontStyle.Bold);
                RenderKit.DrawCentered(ctx, input.TemperatureText, tempFont, titleColor,
                    new PointF(pixelSize / 2f, pixelSize * TemperatureCenterYFraction));
            }
            if (input.LocationLabel.Length > 0)
            {
                var locationFont = font.CreateFont(pixelSize * LocationFontSizeFraction, FontStyle.Regular);
                RenderKit.DrawCentered(ctx, input.LocationLabel, locationFont, titleColor,
                    new PointF(pixelSize / 2f, pixelSize * LocationCenterYFraction));
            }
        });

        return image;
    }

    private enum ConditionGlyph { Clear, Cloud, Rain, Snow }

    /// <summary>Collapses an Open-Meteo WMO code (see OpenMeteoWeatherProvider.ConditionFor) to a glyph family. Unknown codes render clear.</summary>
    private static ConditionGlyph ResolveGlyph(int weatherCode) => weatherCode switch
    {
        0 => ConditionGlyph.Clear,
        1 or 2 or 3 or 45 or 48 => ConditionGlyph.Cloud,
        51 or 53 or 55 or 56 or 57 or 61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 or 95 or 96 or 99 => ConditionGlyph.Rain,
        71 or 73 or 75 or 77 or 85 or 86 => ConditionGlyph.Snow,
        _ => ConditionGlyph.Clear,
    };

    private static void DrawConditionGlyph(IImageProcessingContext ctx, int weatherCode, int size, Color color)
    {
        var center = new PointF(size / 2f, size * GlyphCenterYFraction);
        var radius = size * GlyphRadiusFraction;

        switch (ResolveGlyph(weatherCode))
        {
            case ConditionGlyph.Cloud:
                ctx.Fill(color, RenderKit.BuildCircle(new PointF(center.X - radius * 0.5f, center.Y), radius * 0.7f));
                ctx.Fill(color, RenderKit.BuildCircle(new PointF(center.X + radius * 0.5f, center.Y), radius * 0.7f));
                break;
            case ConditionGlyph.Rain:
                ctx.Fill(color, RenderKit.BuildCircle(center, radius * 0.75f));
                DrawDrops(ctx, center, radius, color);
                break;
            case ConditionGlyph.Snow:
                ctx.Fill(color, RenderKit.BuildCircle(center, radius * 0.75f));
                DrawFlakes(ctx, center, radius, color);
                break;
            default:
                ctx.Fill(color, RenderKit.BuildCircle(center, radius));
                break;
        }
    }

    private static void DrawDrops(IImageProcessingContext ctx, PointF center, float radius, Color color)
    {
        for (var i = -1; i <= 1; i++)
        {
            var x = center.X + i * radius * 0.55f;
            var rect = new RectangleF(x - radius * 0.07f, center.Y + radius * 0.85f, radius * 0.14f, radius * 0.55f);
            ctx.Fill(color, new RectangularPolygon(rect));
        }
    }

    private static void DrawFlakes(IImageProcessingContext ctx, PointF center, float radius, Color color)
    {
        for (var i = -1; i <= 1; i++)
        {
            var x = center.X + i * radius * 0.55f;
            ctx.Fill(color, RenderKit.BuildCircle(new PointF(x, center.Y + radius * 1.05f), radius * 0.14f));
        }
    }
}
