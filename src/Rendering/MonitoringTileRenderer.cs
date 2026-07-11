using System;
using System.Collections.Generic;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Nexus.Service.Rendering;

public enum MonitoringTileStyle { Line, Radial, Number }

/// <summary>
/// Everything <see cref="MonitoringTileRenderer.Render"/> needs to draw one
/// tile, already resolved by the caller (slot label vs sensor display name,
/// title-style overrides vs deck defaults). History is the sample buffer to
/// plot, oldest first, with the current reading as its last entry; an empty
/// buffer renders a blank graph with the current reading treated as 0.
/// </summary>
public sealed class MonitoringTileInput
{
    public string Name { get; init; } = "";
    public bool ShowName { get; init; } = true;
    /// <summary>The sensor's service-formatted display string (HardwareSensor.Formatted). Never reformatted here.</summary>
    public string ValueText { get; init; } = "";
    /// <summary>HardwareSensor.Type (Load, Temperature, Clock, ...), selects the graph/arc domain.</summary>
    public string SensorType { get; init; } = "";
    public IReadOnlyList<float> History { get; init; } = Array.Empty<float>();
    public MonitoringTileStyle Style { get; init; } = MonitoringTileStyle.Line;
    public string? AccentColorHex { get; init; }
    public string? BackgroundColorHex { get; init; }
    /// <summary>"default" | "arial" | "georgia" | "courierNew", matching nexus-web's DECK_TITLE_FONTS ids. Null/unrecognized falls back to the platform default.</summary>
    public string? TitleFont { get; init; }
    /// <summary>Percent of the tile's pixel edge, matching nexus-web's DeckTitleStyle.size convention. Null falls back to the DECK_TITLE_SIZE_DEFAULT (16).</summary>
    public int? TitleSize { get; init; }
    public bool TitleBold { get; init; }
    public bool TitleItalic { get; init; }
    public string? TitleColorHex { get; init; }
}

/// <summary>
/// Draws a monitoring deck tile (sensor name / value / graph) into a square
/// ImageSharp image at any pixel size. Pure: no deck, HID, or persistence
/// knowledge, so it is independently testable and reused for both the web
/// preview parity check and the physical key bitmap.
/// </summary>
internal static class MonitoringTileRenderer
{
    private static readonly Color DefaultBackground = Color.ParseHex("0e1116");
    private static readonly Color DefaultAccent = Color.ParseHex("4da3ff");
    private static readonly Color DefaultTitleColor = Color.White;
    private static readonly Color TrackColor = Color.FromPixel(new Rgba32(255, 255, 255, 40));

    private const int DefaultTitleSizePercent = 16;
    private const int MinTitleSizePercent = 8;
    private const int MaxTitleSizePercent = 30;

    private const float NameYFraction = 0.14f;
    private const float ValueYFraction = 0.88f;
    private const float ValueFontSizeFraction = 0.15f;

    private const float LineBandTopWithName = 0.28f;
    private const float LineBandTopNoName = 0.10f;
    private const float LineBandBottom = 0.74f;
    private const float LineBandInsetXFraction = 0.08f;

    private const float RadialBandTopWithName = 0.26f;
    private const float RadialBandTopNoName = 0.08f;
    private const float RadialBandBottom = 0.76f;
    private const float RadialThicknessFraction = 0.22f;
    private const float RadialStartDeg = 135f;
    private const float RadialSweepDeg = 270f;

    private const float NumberBigFontFraction = 0.30f;
    private const float NumberUnitFontFraction = 0.11f;

    public static Image<Rgba32> Render(MonitoringTileInput input, int pixelSize)
    {
        var image = new Image<Rgba32>(pixelSize, pixelSize);
        var background = RenderKit.ParseColor(input.BackgroundColorHex, DefaultBackground);
        var accent = RenderKit.ParseColor(input.AccentColorHex, DefaultAccent);
        var titleColor = RenderKit.ParseColor(input.TitleColorHex, DefaultTitleColor);
        var titleFont = ResolveTitleFont(input.TitleFont);
        var titleFontStyle = ResolveFontStyle(input.TitleBold, input.TitleItalic);
        var titleSizePx = TitlePixelSize(input.TitleSize, pixelSize);
        var nameShown = input.ShowName && !string.IsNullOrEmpty(input.Name);
        var domain = ResolveDomain(input.SensorType, input.History);

        image.Mutate(ctx =>
        {
            ctx.Fill(background);

            if (nameShown)
            {
                var nameFont = titleFont.CreateFont(titleSizePx, titleFontStyle);
                RenderKit.DrawCentered(ctx, input.Name, nameFont, titleColor, new PointF(pixelSize / 2f, pixelSize * NameYFraction));
            }

            switch (input.Style)
            {
                case MonitoringTileStyle.Number:
                    RenderNumber(ctx, input, pixelSize, titleFont, titleColor, nameShown);
                    break;
                case MonitoringTileStyle.Radial:
                    RenderRadial(ctx, input, pixelSize, accent, domain, nameShown);
                    DrawBottomValue(ctx, input, pixelSize, titleFont, titleColor);
                    break;
                default:
                    RenderLine(ctx, input, pixelSize, accent, domain, nameShown);
                    DrawBottomValue(ctx, input, pixelSize, titleFont, titleColor);
                    break;
            }
        });

        return image;
    }

    private static void DrawBottomValue(IImageProcessingContext ctx, MonitoringTileInput input, int size, FontFamily font, Color color)
    {
        if (string.IsNullOrEmpty(input.ValueText))
        {
            return;
        }
        var valueFont = font.CreateFont(size * ValueFontSizeFraction, FontStyle.Bold);
        RenderKit.DrawCentered(ctx, input.ValueText, valueFont, color, new PointF(size / 2f, size * ValueYFraction));
    }

    private static void RenderLine(IImageProcessingContext ctx, MonitoringTileInput input, int size, Color accent, (float Min, float Max) domain, bool nameShown)
    {
        var bandTop = size * (nameShown ? LineBandTopWithName : LineBandTopNoName);
        var bandBottom = size * LineBandBottom;
        var inset = size * LineBandInsetXFraction;
        var band = new RectangleF(inset, bandTop, size - inset * 2f, bandBottom - bandTop);

        var normalized = new List<float>(input.History.Count);
        foreach (var sample in input.History)
        {
            normalized.Add(Normalize(sample, domain.Min, domain.Max));
        }
        ctx.Fill(accent, RenderKit.BuildFilledSeries(band, normalized));
    }

    private static void RenderRadial(IImageProcessingContext ctx, MonitoringTileInput input, int size, Color accent, (float Min, float Max) domain, bool nameShown)
    {
        var bandTop = size * (nameShown ? RadialBandTopWithName : RadialBandTopNoName);
        var bandBottom = size * RadialBandBottom;
        var bandHeight = bandBottom - bandTop;
        var center = new PointF(size / 2f, bandTop + bandHeight / 2f);
        var outerRadius = Math.Min(size * 0.42f, bandHeight / 2f);
        var innerRadius = outerRadius * (1f - RadialThicknessFraction);

        var current = input.History.Count > 0 ? input.History[^1] : 0f;
        var fraction = Normalize(current, domain.Min, domain.Max);

        ctx.Fill(TrackColor, RenderKit.BuildRingSegment(center, innerRadius, outerRadius, RadialStartDeg, RadialStartDeg + RadialSweepDeg));
        if (fraction > 0f)
        {
            ctx.Fill(accent, RenderKit.BuildRingSegment(center, innerRadius, outerRadius, RadialStartDeg, RadialStartDeg + RadialSweepDeg * fraction));
        }
    }

    private static void RenderNumber(IImageProcessingContext ctx, MonitoringTileInput input, int size, FontFamily font, Color color, bool nameShown)
    {
        var (numberText, unitText) = SplitFormatted(input.ValueText);
        if (numberText.Length == 0)
        {
            return;
        }
        var centerY = size * (nameShown ? 0.58f : 0.52f);
        var bigFont = font.CreateFont(size * NumberBigFontFraction, FontStyle.Bold);
        RenderKit.DrawCentered(ctx, numberText, bigFont, color, new PointF(size / 2f, centerY));

        if (unitText.Length > 0)
        {
            var unitFont = font.CreateFont(size * NumberUnitFontFraction, FontStyle.Regular);
            var unitY = centerY + size * NumberBigFontFraction * 0.62f;
            RenderKit.DrawCentered(ctx, unitText, unitFont, color, new PointF(size / 2f, unitY));
        }
    }

    /// <summary>
    /// Percent -> 0-100, temperature -> 0-100 (both fixed so a graph/arc
    /// reads consistently regardless of how hot the sample window got),
    /// everything else auto-scales to its own history's min/max.
    /// </summary>
    private static (float Min, float Max) ResolveDomain(string sensorType, IReadOnlyList<float> history)
    {
        if (string.Equals(sensorType, "Load", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sensorType, "Temperature", StringComparison.OrdinalIgnoreCase))
        {
            return (0f, 100f);
        }
        if (history.Count == 0)
        {
            return (0f, 1f);
        }
        var min = history[0];
        var max = history[0];
        for (var i = 1; i < history.Count; i++)
        {
            if (history[i] < min)
            {
                min = history[i];
            }
            if (history[i] > max)
            {
                max = history[i];
            }
        }
        return (min, max);
    }

    /// <summary>A degenerate domain (no variation yet, or a single sample) renders at a neutral mid-fill rather than 0 or 100.</summary>
    private static float Normalize(float value, float min, float max)
    {
        if (max <= min)
        {
            return 0.5f;
        }
        return Math.Clamp((value - min) / (max - min), 0f, 1f);
    }

    private static float TitlePixelSize(int? sizePercent, int pixelSize)
    {
        var clamped = Math.Clamp(sizePercent ?? DefaultTitleSizePercent, MinTitleSizePercent, MaxTitleSizePercent);
        return pixelSize * clamped / 100f;
    }

    private static FontStyle ResolveFontStyle(bool bold, bool italic)
    {
        if (bold && italic)
        {
            return FontStyle.BoldItalic;
        }
        if (bold)
        {
            return FontStyle.Bold;
        }
        return italic ? FontStyle.Italic : FontStyle.Regular;
    }

    private static FontFamily ResolveTitleFont(string? fontId)
    {
        var family = fontId switch
        {
            "arial" => TryFamily("Arial"),
            "georgia" => TryFamily("Georgia"),
            "courierNew" => TryFamily("Courier New"),
            _ => null,
        };
        return family ?? RenderKit.ResolveFont();
    }

    private static FontFamily? TryFamily(string name) => SystemFonts.TryGet(name, out var family) ? family : null;

    /// <summary>Splits a leading numeric run (digits, '.', '-', ',') from its trailing unit suffix, e.g. "4713MHz" -> ("4713", "MHz"). Never reformats the value.</summary>
    private static (string Number, string Unit) SplitFormatted(string formatted)
    {
        var i = 0;
        while (i < formatted.Length && (char.IsDigit(formatted[i]) || formatted[i] == '.' || formatted[i] == '-' || formatted[i] == ','))
        {
            i++;
        }
        if (i == 0)
        {
            return (formatted, "");
        }
        return i == formatted.Length ? (formatted, "") : (formatted[..i], formatted[i..]);
    }
}
