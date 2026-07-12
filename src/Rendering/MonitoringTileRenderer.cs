using System;
using System.Collections.Generic;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Nexus.Service.Rendering;

public enum MonitoringTileStyle { Line, Segments, Backdrop, Number }

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
    /// <summary>Percent of the tile's pixel edge, matching nexus-web's DeckTitleStyle.size convention. Null falls back to MonitoringTileRenderer.DefaultTitleSizePercent.</summary>
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

    /// <summary>Mirrors nexus-web's DeckMonitoringCell SEGMENTS_COUNT.</summary>
    private const int SegmentsCount = 16;
    private const float SegmentsGapFraction = 0.014f;

    private const float NumberBigFontFraction = 0.30f;
    private const float NumberUnitFontFraction = 0.11f;

    /// <summary>
    /// Maps a persisted deck action style string to a render style. Legacy
    /// "radial" (the arc style segments replaced) reads as Segments but is
    /// never written back; unrecognized or absent values fall back to Line.
    /// </summary>
    internal static MonitoringTileStyle ParseStyle(string? style) => style switch
    {
        "segments" => MonitoringTileStyle.Segments,
        "radial" => MonitoringTileStyle.Segments,
        "backdrop" => MonitoringTileStyle.Backdrop,
        "number" => MonitoringTileStyle.Number,
        _ => MonitoringTileStyle.Line,
    };

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
                case MonitoringTileStyle.Segments:
                    RenderSegments(ctx, input, pixelSize, accent, domain, nameShown);
                    DrawBottomValue(ctx, input, pixelSize, titleFont, titleColor);
                    break;
                // nexus-web's DeckMonitoringCell renders Backdrop as the same
                // history series and graph band as Line, just filled at full
                // accent opacity with no stroke instead of Line's partial
                // fill plus stroke; RenderLine already fills at full accent
                // opacity with no stroke, so the same call covers both.
                case MonitoringTileStyle.Backdrop:
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

    /// <summary>
    /// Mirrors nexus-web's DeckMonitoringCell: a row of SegmentsCount
    /// pill-shaped bars across the graph band, filled left to right by the
    /// current reading's fill fraction. Reuses the Line band position so the
    /// middle graph area lines up across styles.
    /// </summary>
    private static void RenderSegments(IImageProcessingContext ctx, MonitoringTileInput input, int size, Color accent, (float Min, float Max) domain, bool nameShown)
    {
        var bandTop = size * (nameShown ? LineBandTopWithName : LineBandTopNoName);
        var bandBottom = size * LineBandBottom;
        var inset = size * LineBandInsetXFraction;
        var bandLeft = inset;
        var bandWidth = size - inset * 2f;

        var gap = Math.Max(1f, size * SegmentsGapFraction);
        var segmentWidth = (bandWidth - gap * (SegmentsCount - 1)) / SegmentsCount;
        if (segmentWidth <= 0f)
        {
            return;
        }

        var current = input.History.Count > 0 ? input.History[^1] : 0f;
        var fraction = FillFraction(current, domain);
        var filledCount = (int)Math.Clamp(MathF.Round(fraction * SegmentsCount, MidpointRounding.AwayFromZero), 0f, (float)SegmentsCount);

        for (var i = 0; i < SegmentsCount; i++)
        {
            var x = bandLeft + i * (segmentWidth + gap);
            var rect = new RectangleF(x, bandTop, segmentWidth, bandBottom - bandTop);
            var color = i < filledCount ? accent : TrackColor;
            // Radius clamps to half the bar's width in BuildRoundedRect, so
            // passing the width itself always yields a full pill at this scale.
            ctx.Fill(color, BuildRoundedRect(rect, segmentWidth));
        }
    }

    /// <summary>
    /// Builds a rounded-rectangle polygon from manual corner arcs, the same
    /// approach RenderKit.BuildRingSegment uses, rather than depending on a
    /// PathBuilder rounded-rect overload. Radius clamps to half the shorter
    /// side, so a bar narrower than twice the requested radius renders as a
    /// full pill.
    /// </summary>
    private static IPath BuildRoundedRect(RectangleF rect, float radius)
    {
        var maxRadius = Math.Max(0f, Math.Min(rect.Width, rect.Height) / 2f);
        var r = Math.Clamp(radius, 0f, maxRadius);
        if (r <= 0f)
        {
            return new RectangularPolygon(rect);
        }

        var points = new List<PointF>();
        AddCornerArc(points, rect.Right - r, rect.Top + r, -90f, r);
        AddCornerArc(points, rect.Right - r, rect.Bottom - r, 0f, r);
        AddCornerArc(points, rect.Left + r, rect.Bottom - r, 90f, r);
        AddCornerArc(points, rect.Left + r, rect.Top + r, 180f, r);
        return new Polygon(new LinearLineSegment(points.ToArray()));
    }

    private static void AddCornerArc(List<PointF> points, float cx, float cy, float startDeg, float radius, int segments = 4)
    {
        for (var i = 0; i <= segments; i++)
        {
            var deg = startDeg + 90f * i / segments;
            var rad = deg * MathF.PI / 180f;
            points.Add(new PointF(cx + radius * MathF.Cos(rad), cy + radius * MathF.Sin(rad)));
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
    /// Load/Temperature/Control/Level are fixed 0-100 so a graph/arc reads
    /// consistently regardless of the sample window; everything else
    /// auto-scales to its own history's min/max.
    /// </summary>
    internal static (float Min, float Max) ResolveDomain(string sensorType, IReadOnlyList<float> history)
    {
        if (string.Equals(sensorType, "Load", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sensorType, "Temperature", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sensorType, "Control", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sensorType, "Level", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Single-value fill fraction for value-fill styles (Segments' filled
    /// count): value/domainMax, not a min/max normalization like the line
    /// graph's y-axis, so a fixed 0-100 domain reads as a true percent-of-100
    /// fill. A degenerate domain still renders a neutral mid-fill rather than
    /// 0 or 100.
    /// </summary>
    internal static float FillFraction(float value, (float Min, float Max) domain)
    {
        if (domain.Max <= domain.Min)
        {
            return 0.5f;
        }
        return Math.Clamp(value / domain.Max, 0f, 1f);
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
