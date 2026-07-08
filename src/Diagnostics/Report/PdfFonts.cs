using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Nexus.Service.Diagnostics.Report;

/// <summary>
/// Standard-14 Helvetica / Helvetica-Bold AFM glyph widths (per 1000 em units)
/// plus WinAnsi-safe text handling. Every dynamic string (device names, model
/// strings) is sanitized to the printable ASCII range before it reaches a PDF
/// literal string, so text containing emoji or CJK characters can never emit
/// invalid bytes into the content stream. Non-ASCII typographic punctuation
/// (smart quotes, dashes, the multiplication sign used in memory stick
/// layouts) is transliterated to its plain-ASCII equivalent rather than
/// dropped, so the report stays readable.
/// </summary>
internal static class PdfFonts
{
    public const string Regular = "F1";
    public const string Bold = "F2";

    private static readonly Dictionary<char, short> HelveticaWidths = new()
    {
        [' '] = 278, ['!'] = 278, ['"'] = 355, ['#'] = 556, ['$'] = 556, ['%'] = 889, ['&'] = 667, ['\''] = 191,
        ['('] = 333, [')'] = 333, ['*'] = 389, ['+'] = 584, [','] = 278, ['-'] = 333, ['.'] = 278, ['/'] = 278,
        ['0'] = 556, ['1'] = 556, ['2'] = 556, ['3'] = 556, ['4'] = 556, ['5'] = 556, ['6'] = 556, ['7'] = 556, ['8'] = 556, ['9'] = 556,
        [':'] = 278, [';'] = 278, ['<'] = 584, ['='] = 584, ['>'] = 584, ['?'] = 556, ['@'] = 1015,
        ['A'] = 667, ['B'] = 667, ['C'] = 722, ['D'] = 722, ['E'] = 667, ['F'] = 611, ['G'] = 778, ['H'] = 722, ['I'] = 278, ['J'] = 500,
        ['K'] = 667, ['L'] = 556, ['M'] = 833, ['N'] = 722, ['O'] = 778, ['P'] = 667, ['Q'] = 778, ['R'] = 722, ['S'] = 667, ['T'] = 611,
        ['U'] = 722, ['V'] = 667, ['W'] = 944, ['X'] = 667, ['Y'] = 667, ['Z'] = 611,
        ['['] = 278, ['\\'] = 278, [']'] = 278, ['^'] = 469, ['_'] = 556, ['`'] = 333,
        ['a'] = 556, ['b'] = 556, ['c'] = 500, ['d'] = 556, ['e'] = 556, ['f'] = 278, ['g'] = 556, ['h'] = 556, ['i'] = 222, ['j'] = 222,
        ['k'] = 500, ['l'] = 222, ['m'] = 833, ['n'] = 556, ['o'] = 556, ['p'] = 556, ['q'] = 556, ['r'] = 333, ['s'] = 500, ['t'] = 278,
        ['u'] = 556, ['v'] = 500, ['w'] = 722, ['x'] = 500, ['y'] = 500, ['z'] = 500,
        ['{'] = 334, ['|'] = 260, ['}'] = 334, ['~'] = 584,
    };

    private static readonly Dictionary<char, short> HelveticaBoldWidths = new()
    {
        [' '] = 278, ['!'] = 333, ['"'] = 474, ['#'] = 556, ['$'] = 556, ['%'] = 889, ['&'] = 722, ['\''] = 238,
        ['('] = 333, [')'] = 333, ['*'] = 389, ['+'] = 584, [','] = 278, ['-'] = 333, ['.'] = 278, ['/'] = 278,
        ['0'] = 556, ['1'] = 556, ['2'] = 556, ['3'] = 556, ['4'] = 556, ['5'] = 556, ['6'] = 556, ['7'] = 556, ['8'] = 556, ['9'] = 556,
        [':'] = 333, [';'] = 333, ['<'] = 584, ['='] = 584, ['>'] = 584, ['?'] = 611, ['@'] = 975,
        ['A'] = 722, ['B'] = 722, ['C'] = 722, ['D'] = 722, ['E'] = 667, ['F'] = 611, ['G'] = 778, ['H'] = 722, ['I'] = 278, ['J'] = 556,
        ['K'] = 722, ['L'] = 611, ['M'] = 833, ['N'] = 722, ['O'] = 778, ['P'] = 667, ['Q'] = 778, ['R'] = 722, ['S'] = 667, ['T'] = 611,
        ['U'] = 722, ['V'] = 667, ['W'] = 944, ['X'] = 667, ['Y'] = 667, ['Z'] = 611,
        ['['] = 333, ['\\'] = 278, [']'] = 333, ['^'] = 584, ['_'] = 556, ['`'] = 333,
        ['a'] = 556, ['b'] = 611, ['c'] = 556, ['d'] = 611, ['e'] = 556, ['f'] = 333, ['g'] = 611, ['h'] = 611, ['i'] = 278, ['j'] = 278,
        ['k'] = 556, ['l'] = 278, ['m'] = 889, ['n'] = 611, ['o'] = 611, ['p'] = 611, ['q'] = 611, ['r'] = 389, ['s'] = 556, ['t'] = 333,
        ['u'] = 611, ['v'] = 556, ['w'] = 778, ['x'] = 556, ['y'] = 556, ['z'] = 500,
        ['{'] = 389, ['|'] = 280, ['}'] = 389, ['~'] = 584,
    };

    // Non-ASCII typographic code points, addressed by codepoint cast (never a
    // raw literal glyph in this source file) so common punctuation degrades to
    // its plain-ASCII look instead of a bare '?'.
    private static readonly char EnDash = (char)0x2013;
    private static readonly char EmDash = (char)0x2014;
    private static readonly char LeftSingleQuote = (char)0x2018;
    private static readonly char RightSingleQuote = (char)0x2019;
    private static readonly char LeftDoubleQuote = (char)0x201C;
    private static readonly char RightDoubleQuote = (char)0x201D;
    private static readonly char Bullet = (char)0x2022;
    private static readonly char MultiplicationSign = (char)0x00D7;
    private static readonly char NoBreakSpace = (char)0x00A0;
    private static readonly char Ellipsis = (char)0x2026;

    private static readonly Dictionary<char, char> Transliterate = BuildTransliterateMap();

    private static Dictionary<char, char> BuildTransliterateMap()
    {
        var map = new Dictionary<char, char>
        {
            [EnDash] = '-',
            [EmDash] = '-',
            [LeftSingleQuote] = '\'',
            [RightSingleQuote] = '\'',
            [LeftDoubleQuote] = '"',
            [RightDoubleQuote] = '"',
            [Bullet] = '*',
            [MultiplicationSign] = 'x',
            [NoBreakSpace] = ' ',
        };
        return map;
    }

    /// <summary>Restricts arbitrary text to the printable ASCII range this
    /// module has width metrics for. Known typographic substitutes map to
    /// their plain-ASCII equivalent; everything else (emoji, CJK, other
    /// scripts) becomes a single '?' per code point, including surrogate pairs.</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var normalized = text.Replace(Ellipsis.ToString(), "...", System.StringComparison.Ordinal);
        var sb = new StringBuilder(normalized.Length);
        for (var i = 0; i < normalized.Length; i++)
        {
            var ch = normalized[i];
            if (ch is >= (char)0x20 and <= (char)0x7E)
            {
                sb.Append(ch);
                continue;
            }

            if (Transliterate.TryGetValue(ch, out var mapped))
            {
                sb.Append(mapped);
                continue;
            }

            if (char.IsHighSurrogate(ch) && i + 1 < normalized.Length && char.IsLowSurrogate(normalized[i + 1]))
            {
                i++;
            }
            sb.Append('?');
        }
        return sb.ToString();
    }

    /// <summary>Escapes the three PDF literal-string special characters. Call
    /// last - after <see cref="Sanitize"/> and after any
    /// <see cref="MeasureWidthPt"/>/<see cref="TruncateToWidth"/> call, since
    /// the inserted backslash bytes would otherwise count toward the measured
    /// width and shift the truncation point.</summary>
    public static string Escape(string sanitizedAscii)
    {
        var sb = new StringBuilder(sanitizedAscii.Length);
        foreach (var ch in sanitizedAscii)
        {
            if (ch is '(' or ')' or '\\')
            {
                sb.Append('\\');
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Sanitize + escape in one call, for the common case of embedding
    /// a raw dynamic string directly into a PDF literal string.</summary>
    public static string ToLiteral(string? raw) => Escape(Sanitize(raw));

    private static double CharWidthPt(char ch, bool bold, double sizePt)
    {
        var table = bold ? HelveticaBoldWidths : HelveticaWidths;
        var width = table.TryGetValue(ch, out var found) ? found : table['?'];
        return width / 1000.0 * sizePt;
    }

    /// <summary>Measures already-sanitized ASCII text; unsanitized input measures
    /// each non-ASCII char as if it were '?' (the same fallback Sanitize uses).</summary>
    public static double MeasureWidthPt(string text, bool bold, double sizePt)
    {
        double total = 0;
        foreach (var ch in text)
        {
            total += CharWidthPt(ch, bold, sizePt);
        }
        return total;
    }

    /// <summary>Truncates already-sanitized text to fit maxWidthPt, appending
    /// "..." when it does not fit. Returns the input unchanged if it already fits.</summary>
    public static string TruncateToWidth(string sanitizedAscii, bool bold, double sizePt, double maxWidthPt)
    {
        if (MeasureWidthPt(sanitizedAscii, bold, sizePt) <= maxWidthPt)
        {
            return sanitizedAscii;
        }

        const string ellipsis = "...";
        var ellipsisWidth = MeasureWidthPt(ellipsis, bold, sizePt);
        var budget = maxWidthPt - ellipsisWidth;
        if (budget <= 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        double width = 0;
        foreach (var ch in sanitizedAscii)
        {
            var w = CharWidthPt(ch, bold, sizePt);
            if (width + w > budget)
            {
                break;
            }
            sb.Append(ch);
            width += w;
        }
        return sb + ellipsis;
    }

    /// <summary>Formats a point/em value as PDF-safe, invariant-culture, minimal-decimal text.</summary>
    public static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Formats a small ratio (e.g. a `cm` matrix scale coefficient) with
    /// enough decimal places that a small source/target ratio does not round
    /// away to a visibly wrong scale, or to zero.</summary>
    public static string NumPrecise(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
}
