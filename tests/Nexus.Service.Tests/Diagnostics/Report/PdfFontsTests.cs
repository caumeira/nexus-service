using Nexus.Service.Diagnostics.Report;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Report;

public class PdfFontsTests
{
    [Fact]
    public void Sanitize_EmojiAndKanji_ProducesOnlyPrintableAscii()
    {
        var deviceName = "\U0001F525 Cooler キーボード RGB";

        var sanitized = PdfFonts.Sanitize(deviceName);

        foreach (var ch in sanitized)
        {
            Assert.InRange(ch, (char)0x20, (char)0x7E);
        }
        Assert.Contains("Cooler", sanitized);
        Assert.Contains("RGB", sanitized);
        Assert.DoesNotContain("\U0001F525", sanitized);
    }

    [Fact]
    public void Sanitize_TypographicDashesAndQuotes_TransliterateToAscii()
    {
        var text = "2 " + (char)0x00D7 + " 16 GB " + (char)0x2013 + " Corsair " + (char)0x2018 + "Dominator" + (char)0x2019;

        var sanitized = PdfFonts.Sanitize(text);

        Assert.Equal("2 x 16 GB - Corsair 'Dominator'", sanitized);
    }

    [Fact]
    public void Escape_ParensAndBackslash_ArePrefixedWithBackslash()
    {
        var escaped = PdfFonts.Escape("a(b)c\\d");
        Assert.Equal("a\\(b\\)c\\\\d", escaped);
    }

    [Fact]
    public void TruncateToWidth_FitsUnchanged_WhenAlreadyWithinBudget()
    {
        var text = "short";
        var wide = PdfFonts.MeasureWidthPt(text, false, 8) + 10;
        Assert.Equal(text, PdfFonts.TruncateToWidth(text, false, 8, wide));
    }

    [Fact]
    public void TruncateToWidth_TooLong_EndsWithEllipsisAndFitsBudget()
    {
        var text = "a very long device model name that will not fit";
        var budget = PdfFonts.MeasureWidthPt(text, false, 8) / 4;

        var truncated = PdfFonts.TruncateToWidth(text, false, 8, budget);

        Assert.EndsWith("...", truncated);
        Assert.True(PdfFonts.MeasureWidthPt(truncated, false, 8) <= budget);
    }

    [Fact]
    public void MeasureWidthPt_UnknownCharacterFallsBackToQuestionMarkWidth()
    {
        var questionMarkWidth = PdfFonts.MeasureWidthPt("?", false, 10);
        var nonAsciiWidth = PdfFonts.MeasureWidthPt(((char)0x00E9).ToString(), false, 10);
        Assert.Equal(questionMarkWidth, nonAsciiWidth);
    }
}
