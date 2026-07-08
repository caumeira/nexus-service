using System.Text;

namespace Nexus.Service.Diagnostics.Report;

/// <summary>
/// Accumulates one page's content-stream operators as plain text. Every
/// character this class emits is either a fixed ASCII operator or dynamic
/// text already restricted to ASCII 0x20-0x7E by <see cref="PdfFonts"/>, so
/// the whole stream is built as a string and ASCII-encoded once at the end -
/// no byte-level escaping needed here. Left uncompressed on purpose (the
/// report is small; readable content streams are easier to debug and test).
/// </summary>
internal sealed class PdfContentBuilder
{
    private readonly StringBuilder _sb = new();

    public void FillGray(double level)
    {
        _sb.Append(PdfFonts.Num(level)).Append(" g\n");
    }

    public void StrokeGray(double level)
    {
        _sb.Append(PdfFonts.Num(level)).Append(" G\n");
    }

    public void LineWidth(double widthPt)
    {
        _sb.Append(PdfFonts.Num(widthPt)).Append(" w\n");
    }

    /// <summary>Strokes a single straight line; caller sets gray/width first.</summary>
    public void Line(double x1, double y1, double x2, double y2)
    {
        _sb.Append(PdfFonts.Num(x1)).Append(' ').Append(PdfFonts.Num(y1)).Append(" m ")
           .Append(PdfFonts.Num(x2)).Append(' ').Append(PdfFonts.Num(y2)).Append(" l S\n");
    }

    /// <summary>Left-anchored text at baseline (x, y). Caller passes already
    /// sanitized text; this escapes it for the PDF literal string.</summary>
    public void Text(double x, double y, double sizePt, bool bold, string sanitizedText)
    {
        var font = bold ? PdfFonts.Bold : PdfFonts.Regular;
        _sb.Append("BT /").Append(font).Append(' ').Append(PdfFonts.Num(sizePt)).Append(" Tf ")
           .Append(PdfFonts.Num(x)).Append(' ').Append(PdfFonts.Num(y)).Append(" Td (")
           .Append(PdfFonts.Escape(sanitizedText)).Append(") Tj ET\n");
    }

    /// <summary>Right-anchored text: rightX is the right edge the text ends at.</summary>
    public void TextRightAligned(double rightX, double y, double sizePt, bool bold, string sanitizedText)
    {
        var width = PdfFonts.MeasureWidthPt(sanitizedText, bold, sizePt);
        Text(rightX - width, y, sizePt, bold, sanitizedText);
    }

    /// <summary>Places an image XObject into rect (x, y, w, h), (x, y) = bottom-left.</summary>
    public void DrawImage(string resourceName, double x, double y, double w, double h)
    {
        _sb.Append("q ").Append(PdfFonts.Num(w)).Append(" 0 0 ").Append(PdfFonts.Num(h)).Append(' ')
           .Append(PdfFonts.Num(x)).Append(' ').Append(PdfFonts.Num(y))
           .Append(" cm /").Append(resourceName).Append(" Do Q\n");
    }

    /// <summary>
    /// Draws a pre-baked vector path (m/l/c/h operators in an arbitrary source
    /// unit space, e.g. an SVG viewBox) scaled and translated into rect (x, y,
    /// w, h) with a y-flip (source is top-down, PDF is bottom-up), then fills
    /// it with the given gray level.
    /// </summary>
    public void DrawVectorPath(string bakedPathOps, double sourceWidth, double sourceHeight,
        double x, double y, double w, double h, double grayLevel)
    {
        var a = w / sourceWidth;
        var d = -(h / sourceHeight);
        var e = x;
        var f = y + h;

        _sb.Append(PdfFonts.Num(grayLevel)).Append(" g\n");
        _sb.Append("q ").Append(PdfFonts.Num(a)).Append(" 0 0 ").Append(PdfFonts.Num(d)).Append(' ')
           .Append(PdfFonts.Num(e)).Append(' ').Append(PdfFonts.Num(f)).Append(" cm\n");
        _sb.Append(bakedPathOps);
        _sb.Append("f\nQ\n");
    }

    public byte[] ToBytes() => Encoding.ASCII.GetBytes(_sb.ToString());
}
