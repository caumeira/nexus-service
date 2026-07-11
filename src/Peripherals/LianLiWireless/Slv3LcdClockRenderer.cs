using System;
using System.Globalization;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Nexus.Service.Rendering;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Renders a 400x400 JPEG clock face for the SL-LCD Wireless screen: two
/// digital faces and two analog faces, a subset of L-Connect's seven
/// Clock1..7 themes. Pure: takes the instant to render, so tests don't need
/// to mock the system clock.
/// </summary>
public static class Slv3LcdClockRenderer
{
    public const int Width = Slv3LcdProtocol.PanelWidth;
    public const int Height = Slv3LcdProtocol.PanelHeight;

    private static readonly Color DefaultAccent = Color.ParseHex("#00D1FF");
    private static readonly Color DefaultText = Color.White;

    public static byte[] Render(string? face, DateTime now, string? accentHex, string? textHex)
    {
        var accent = RenderKit.ParseColor(accentHex, DefaultAccent);
        var text = RenderKit.ParseColor(textHex, DefaultText);

        return face switch
        {
            "digitalMinimal" => RenderDigital(now, accent, text, showDate: false),
            "analogClassic" => RenderAnalog(now, accent, text, showNumbers: true),
            "analogMinimal" => RenderAnalog(now, accent, text, showNumbers: false),
            // "digital" and any unrecognized/missing face default to the full digital face.
            _ => RenderDigital(now, accent, text, showDate: true),
        };
    }

    private static byte[] RenderDigital(DateTime now, Color accent, Color text, bool showDate)
    {
        using var image = new Image<Rgba32>(Width, Height);
        var font = RenderKit.ResolveFont();
        var center = new PointF(Width / 2f, Height / 2f);

        image.Mutate(ctx =>
        {
            ctx.Fill(Color.Black);
            var timeY = showDate ? center.Y - 20f : center.Y;
            RenderKit.DrawCentered(ctx, now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), font.CreateFont(64, FontStyle.Bold), accent, new PointF(center.X, timeY));
            if (showDate)
            {
                RenderKit.DrawCentered(ctx, now.ToString("ddd, MMM d", CultureInfo.InvariantCulture), font.CreateFont(26, FontStyle.Regular), text, new PointF(center.X, timeY + 60f));
            }
        });

        return RenderKit.EncodeJpeg(image);
    }

    private static byte[] RenderAnalog(DateTime now, Color accent, Color text, bool showNumbers)
    {
        using var image = new Image<Rgba32>(Width, Height);
        var font = RenderKit.ResolveFont();
        var center = new PointF(Width / 2f, Height / 2f);
        const float dialRadius = 175f;

        var hourDeg = (now.Hour % 12 + now.Minute / 60f) * 30f;
        var minuteDeg = (now.Minute + now.Second / 60f) * 6f;
        var secondDeg = now.Second * 6f;

        image.Mutate(ctx =>
        {
            ctx.Fill(Color.Black);
            ctx.Draw(text, 3f, RenderKit.BuildCircle(center, dialRadius));

            for (var tick = 0; tick < 12; tick++)
            {
                var tickDeg = tick * 30f;
                ctx.Fill(text, RenderKit.BuildClockTick(center, dialRadius - 24f, dialRadius - 6f, tickDeg, 6f));
                if (showNumbers && tick % 3 == 0)
                {
                    var rad = (tickDeg - 90f) * MathF.PI / 180f;
                    var numberRadius = dialRadius - 34f;
                    var point = new PointF(center.X + numberRadius * MathF.Cos(rad), center.Y + numberRadius * MathF.Sin(rad));
                    var hourNumber = tick == 0 ? 12 : tick;
                    RenderKit.DrawCentered(ctx, hourNumber.ToString(CultureInfo.InvariantCulture), font.CreateFont(24, FontStyle.Regular), text, point);
                }
            }

            ctx.Fill(text, RenderKit.BuildHand(center, dialRadius * 0.5f, hourDeg, 10f));
            ctx.Fill(text, RenderKit.BuildHand(center, dialRadius * 0.75f, minuteDeg, 7f));
            ctx.Fill(accent, RenderKit.BuildHand(center, dialRadius * 0.82f, secondDeg, 3f, tailFraction: 0.18f));
            ctx.Fill(accent, RenderKit.BuildCircle(center, 8f));
        });

        return RenderKit.EncodeJpeg(image);
    }
}
