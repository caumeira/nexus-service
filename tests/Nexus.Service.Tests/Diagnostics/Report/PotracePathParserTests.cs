using Nexus.Service.Diagnostics.Report;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Report;

public class PotracePathParserTests
{
    [Fact]
    public void Parse_MoveLineCurveClose_ProducesExpectedAbsoluteOps()
    {
        var ops = PotracePathParser.Parse("M0 0 l10 0 c0 5 5 10 10 10 z");

        Assert.Collection(ops,
            op => Assert.Equal(new PathOp.MoveTo(0, 0), op),
            op => Assert.Equal(new PathOp.LineTo(10, 0), op),
            op => Assert.Equal(new PathOp.CurveTo(10, 5, 15, 10, 20, 10), op),
            op => Assert.IsType<PathOp.ClosePath>(op));
    }

    [Fact]
    public void Parse_RepeatedLinetoCoordinates_EachPairIsALineTo()
    {
        var ops = PotracePathParser.Parse("M0 0 l10 0 10 0 10 0");

        Assert.Collection(ops,
            op => Assert.Equal(new PathOp.MoveTo(0, 0), op),
            op => Assert.Equal(new PathOp.LineTo(10, 0), op),
            op => Assert.Equal(new PathOp.LineTo(20, 0), op),
            op => Assert.Equal(new PathOp.LineTo(30, 0), op));
    }

    [Fact]
    public void Parse_AbsoluteMoveThenAbsoluteLine_DoesNotAccumulateRelativeOffsets()
    {
        var ops = PotracePathParser.Parse("M100 200 L150 250");

        Assert.Collection(ops,
            op => Assert.Equal(new PathOp.MoveTo(100, 200), op),
            op => Assert.Equal(new PathOp.LineTo(150, 250), op));
    }

    [Fact]
    public void Parse_ClosePath_ResetsCurrentPointToSubpathStart()
    {
        var ops = PotracePathParser.Parse("M5 5 l10 0 z l1 1");

        Assert.Collection(ops,
            op => Assert.Equal(new PathOp.MoveTo(5, 5), op),
            op => Assert.Equal(new PathOp.LineTo(15, 5), op),
            op => Assert.IsType<PathOp.ClosePath>(op),
            op => Assert.Equal(new PathOp.LineTo(6, 6), op));
    }

    [Theory]
    [InlineData("M0 0 Q10 10 20 20")]
    [InlineData("M0 0 S10 10 20 20")]
    [InlineData("M0 0 A5 5 0 0 1 10 10")]
    public void Parse_UnsupportedCommand_Throws(string pathData)
    {
        Assert.Throws<System.NotSupportedException>(() => PotracePathParser.Parse(pathData));
    }

    [Fact]
    public void Parse_LinetoMissingYCoordinate_ThrowsFormatException()
    {
        // "l10" reads x=10 then finds no y before the string ends.
        Assert.Throws<System.FormatException>(() => PotracePathParser.Parse("M0 0 l10"));
    }
}
