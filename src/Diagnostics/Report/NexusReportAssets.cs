using System;
using System.IO;
using System.Reflection;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Nexus.Service.Diagnostics.Report;

/// <summary>
/// Static report assets, each decoded/parsed once per process and cached: the
/// embedded colored Nexus mark (as separate RGB + alpha planes ready for a PDF
/// Image XObject + its SMask) and the baked NEXUS wordmark vector path
/// operators. Neither depends on where the report places them on the page -
/// <see cref="PdfContentBuilder.DrawVectorPath"/> applies the placement
/// transform at draw time.
/// </summary>
internal static class NexusReportAssets
{
    private const string MarkResourceName = "nexus-mark-color.png";

    // WORDMARK_VIEWBOX in nexus-web/src/components/icons/NexusBrand.tsx.
    public const double WordmarkSourceWidth = 1526;
    public const double WordmarkSourceHeight = 373;

    // The <g transform="translate(...) scale(...)"> wrapping WORDMARK_PATHS in
    // NexusBrand.tsx: potrace's own raw-trace-unit -> viewBox-space transform.
    // Order matters: SVG applies scale first, then translate.
    private const double GTranslateX = -141.000000;
    private const double GTranslateY = 499.240329;
    private const double GScaleX = 0.100000;
    private const double GScaleY = -0.100000;

    // Copied verbatim from WORDMARK_PATHS's five <path d="..."> strings in
    // nexus-web/src/components/icons/NexusBrand.tsx (potrace trace of
    // /nexus/NexusWordMark.png). Keep both copies in sync if the trace is redone.
    private static readonly string[] WordmarkPathData =
    {
        @"M15380 4980 c-755 -105 -1169 -589 -1056 -1239 73 -418 297 -605
1020 -847 501 -168 632 -266 668 -498 98 -633 -896 -806 -1372 -239 l-35 41
-215 -199 c-118 -110 -221 -206 -229 -214 -54 -53 340 -323 605 -415 1064
-367 2038 258 1884 1208 -70 429 -308 622 -1090 881 -457 152 -580 249 -605
477 -59 526 757 715 1211 282 l62 -59 203 222 c232 254 223 223 93 322 -281
213 -761 330 -1144 277z",
        @"M1410 3130 l0 -1770 315 0 315 0 0 1366 c0 751 3 1363 8 1360 4 -2
383 -617 842 -1365 l835 -1360 403 -1 402 0 0 1770 0 1770 -315 0 -315 0 -2
-1326 -3 -1327 -822 1324 -821 1324 -421 3 -421 2 0 -1770z",
        @"M5060 3130 l0 -1770 1220 0 1220 0 0 280 0 280 -905 0 -905 0 0 500
0 500 815 0 815 0 0 265 0 265 -815 0 -815 0 0 450 0 450 860 0 860 0 0 275 0
275 -1175 0 -1175 0 0 -1770z",
        @"M7820 4895 c0 -3 246 -374 546 -824 l547 -820 -599 -923 c-328 -508
-604 -933 -612 -946 l-14 -22 383 0 384 0 410 690 c226 379 412 689 415 689 3
0 193 -303 424 -674 230 -371 424 -682 430 -690 10 -13 62 -15 409 -13 l397 3
-630 930 c-354 522 -629 937 -627 946 2 8 240 380 529 825 288 445 528 815
532 822 6 10 -66 12 -359 10 l-367 -3 -354 -595 c-194 -327 -356 -594 -360
-593 -4 1 -168 268 -365 595 l-358 593 -380 3 c-210 1 -381 0 -381 -3z",
        @"M11070 3709 c0 -1294 0 -1293 56 -1487 171 -593 682 -946 1369 -946
631 0 1090 280 1315 802 111 257 110 238 110 1630 l0 1192 -315 0 -315 0 0
-1127 c0 -1337 0 -1337 -106 -1552 -254 -516 -1124 -516 -1378 0 -106 215
-106 217 -106 1552 l0 1127 -315 0 -315 0 0 -1191z",
    };

    private static readonly Lazy<DecodedMark> MarkLazy = new(DecodeMark);
    private static readonly Lazy<string> WordmarkOpsLazy = new(BuildWordmarkOperators);

    public static DecodedMark LoadMark() => MarkLazy.Value;

    public static string LoadWordmarkOperators() => WordmarkOpsLazy.Value;

    public sealed record DecodedMark(int Width, int Height, byte[] RgbFlate, byte[] AlphaFlate);

    private static DecodedMark DecodeMark()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(MarkResourceName)
            ?? throw new InvalidOperationException($"embedded resource not found: {MarkResourceName}");
        using var image = Image.Load<Rgba32>(stream);

        var width = image.Width;
        var height = image.Height;
        var rgb = new byte[width * height * 3];
        var alpha = new byte[width * height];

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var rowBase = y * width;
                for (var x = 0; x < row.Length; x++)
                {
                    var px = row[x];
                    var rgbOffset = (rowBase + x) * 3;
                    rgb[rgbOffset] = px.R;
                    rgb[rgbOffset + 1] = px.G;
                    rgb[rgbOffset + 2] = px.B;
                    alpha[rowBase + x] = px.A;
                }
            }
        });

        return new DecodedMark(width, height, PdfWriter.Deflate(rgb), PdfWriter.Deflate(alpha));
    }

    private static string BuildWordmarkOperators()
    {
        var sb = new StringBuilder();
        foreach (var d in WordmarkPathData)
        {
            foreach (var op in PotracePathParser.Parse(d))
            {
                AppendOp(sb, op);
            }
        }
        return sb.ToString();
    }

    private static void AppendOp(StringBuilder sb, PathOp op)
    {
        switch (op)
        {
            case PathOp.MoveTo m:
            {
                var (x, y) = ToViewBox(m.X, m.Y);
                sb.Append(PdfFonts.Num(x)).Append(' ').Append(PdfFonts.Num(y)).Append(" m\n");
                break;
            }
            case PathOp.LineTo l:
            {
                var (x, y) = ToViewBox(l.X, l.Y);
                sb.Append(PdfFonts.Num(x)).Append(' ').Append(PdfFonts.Num(y)).Append(" l\n");
                break;
            }
            case PathOp.CurveTo c:
            {
                var (x1, y1) = ToViewBox(c.X1, c.Y1);
                var (x2, y2) = ToViewBox(c.X2, c.Y2);
                var (x3, y3) = ToViewBox(c.X3, c.Y3);
                sb.Append(PdfFonts.Num(x1)).Append(' ').Append(PdfFonts.Num(y1)).Append(' ')
                  .Append(PdfFonts.Num(x2)).Append(' ').Append(PdfFonts.Num(y2)).Append(' ')
                  .Append(PdfFonts.Num(x3)).Append(' ').Append(PdfFonts.Num(y3)).Append(" c\n");
                break;
            }
            case PathOp.ClosePath:
                sb.Append("h\n");
                break;
        }
    }

    private static (double X, double Y) ToViewBox(double rawX, double rawY) =>
        (rawX * GScaleX + GTranslateX, rawY * GScaleY + GTranslateY);
}
