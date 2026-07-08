using System.Globalization;
using System.Text.RegularExpressions;
using Nexus.Service.Diagnostics.Report;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Report;

public class PdfContentBuilderTests
{
    [Fact]
    public void DrawVectorPath_SmallScaleRatio_KeepsEnoughPrecisionToStayAccurate()
    {
        var builder = new PdfContentBuilder();
        // Mirrors the header wordmark's real ratio: a 20pt-tall target drawn
        // from a 373-unit-tall source (20/373 = 0.05362...). At "0.##"
        // precision this rounds to 0.05, a ~7% scale error.
        builder.DrawVectorPath("100 100 m\n200 200 l\nh\n", 1526, 373, 40, 700, 81.79, 20, 0.067);
        var content = System.Text.Encoding.ASCII.GetString(builder.ToBytes());

        var match = Regex.Match(content, @"q (-?[0-9.]+) 0 0 (-?[0-9.]+) ");
        Assert.True(match.Success, "cm matrix not found");

        var a = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var d = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);

        Assert.Equal(81.79 / 1526, a, precision: 4);
        Assert.Equal(-(20.0 / 373), d, precision: 4);
    }
}
