using Qos.Service.Cooling;
using Qos.Service.Persistence;

namespace Qos.Service.Tests;

/// <summary>
/// Pure-math tests for the curve evaluation formulas the cooling engine runs
/// every tick. These drive fans in hardware -- regressions here silently push
/// fan duty to wrong values without crashing, so edge-case coverage is worth
/// more than integration tests.
/// </summary>
public class CurveEvaluationTests
{
    // ── Flat ──────────────────────────────────────────────────────────────
    [Fact]
    public void Flat_Null_ReturnsNull()
    {
        Assert.Null(CurveEngine.EvaluateFlat(null));
    }

    [Fact]
    public void Flat_ReturnsConfiguredSpeed()
    {
        Assert.Equal(42, CurveEngine.EvaluateFlat(new FlatCurveData { Speed = 42 }));
    }

    // ── Linear ────────────────────────────────────────────────────────────
    [Fact]
    public void Linear_Null_ReturnsNull()
    {
        Assert.Null(CurveEngine.EvaluateLinear(null, 50f));
    }

    [Fact]
    public void Linear_BelowMin_ReturnsMinSpeed()
    {
        var linear = new LinearCurveData { MinTemp = 40, MaxTemp = 80, MinSpeed = 30, MaxSpeed = 90 };
        Assert.Equal(30.0, CurveEngine.EvaluateLinear(linear, 20f));
    }

    [Fact]
    public void Linear_AboveMax_ReturnsMaxSpeed()
    {
        var linear = new LinearCurveData { MinTemp = 40, MaxTemp = 80, MinSpeed = 30, MaxSpeed = 90 };
        Assert.Equal(90.0, CurveEngine.EvaluateLinear(linear, 100f));
    }

    [Fact]
    public void Linear_Midpoint_Interpolates()
    {
        var linear = new LinearCurveData { MinTemp = 40, MaxTemp = 80, MinSpeed = 30, MaxSpeed = 90 };
        // temp=60 is halfway between 40 and 80; speed should be halfway between 30 and 90
        Assert.Equal(60.0, CurveEngine.EvaluateLinear(linear, 60f));
    }

    [Fact]
    public void Linear_ExactBounds_AreInclusive()
    {
        var linear = new LinearCurveData { MinTemp = 40, MaxTemp = 80, MinSpeed = 30, MaxSpeed = 90 };
        Assert.Equal(30.0, CurveEngine.EvaluateLinear(linear, 40f));
        Assert.Equal(90.0, CurveEngine.EvaluateLinear(linear, 80f));
    }

    [Fact]
    public void Linear_InvertedRange_DoesNotCrash()
    {
        // Defensive: a misconfigured curve with Max < Min shouldn't NaN.
        var linear = new LinearCurveData { MinTemp = 80, MaxTemp = 40, MinSpeed = 30, MaxSpeed = 90 };
        var result = CurveEngine.EvaluateLinear(linear, 60f);
        Assert.NotNull(result);
        Assert.False(double.IsNaN(result!.Value));
    }

    // ── Graph ─────────────────────────────────────────────────────────────
    [Fact]
    public void Graph_Null_ReturnsNull()
    {
        Assert.Null(CurveEngine.EvaluateGraph(null, 50f));
    }

    [Fact]
    public void Graph_Empty_ReturnsNull()
    {
        var graph = new GraphCurveData { Points = new() };
        Assert.Null(CurveEngine.EvaluateGraph(graph, 50f));
    }

    [Fact]
    public void Graph_BelowFirstPoint_ReturnsFirstSpeed()
    {
        var graph = new GraphCurveData
        {
            SpeedModifier = 1.0,
            Points = new()
            {
                new() { Temp = 30, Speed = 20 },
                new() { Temp = 60, Speed = 60 },
                new() { Temp = 90, Speed = 100 },
            },
        };
        Assert.Equal(20.0, CurveEngine.EvaluateGraph(graph, 10f));
    }

    [Fact]
    public void Graph_AboveLastPoint_ReturnsLastSpeed()
    {
        var graph = new GraphCurveData
        {
            SpeedModifier = 1.0,
            Points = new()
            {
                new() { Temp = 30, Speed = 20 },
                new() { Temp = 90, Speed = 100 },
            },
        };
        Assert.Equal(100.0, CurveEngine.EvaluateGraph(graph, 200f));
    }

    [Fact]
    public void Graph_BetweenPoints_Interpolates()
    {
        var graph = new GraphCurveData
        {
            SpeedModifier = 1.0,
            Points = new()
            {
                new() { Temp = 30, Speed = 20 },
                new() { Temp = 70, Speed = 60 },
            },
        };
        // temp=50 is halfway between 30 and 70; speed halfway between 20 and 60 = 40
        Assert.Equal(40.0, CurveEngine.EvaluateGraph(graph, 50f));
    }

    [Fact]
    public void Graph_SpeedModifier_Scales()
    {
        var graph = new GraphCurveData
        {
            SpeedModifier = 0.5,
            Points = new() { new() { Temp = 30, Speed = 100 } },
        };
        Assert.Equal(50.0, CurveEngine.EvaluateGraph(graph, 30f));
    }

    [Fact]
    public void Graph_DuplicatedTemps_DoesNotDivByZero()
    {
        var graph = new GraphCurveData
        {
            SpeedModifier = 1.0,
            Points = new()
            {
                new() { Temp = 50, Speed = 30 },
                new() { Temp = 50, Speed = 70 },
            },
        };
        var result = CurveEngine.EvaluateGraph(graph, 50f);
        Assert.NotNull(result);
        Assert.False(double.IsNaN(result!.Value));
        Assert.False(double.IsInfinity(result.Value));
    }

    [Fact]
    public void Graph_UnsortedPoints_AreSortedInternally()
    {
        var graph = new GraphCurveData
        {
            SpeedModifier = 1.0,
            Points = new()
            {
                new() { Temp = 90, Speed = 100 },
                new() { Temp = 30, Speed = 20 },
                new() { Temp = 60, Speed = 60 },
            },
        };
        // After sort, temp=50 should interpolate between 30->60 points
        // (20,60) with t=(50-30)/(60-30)=0.667; speed=20+0.667*(60-20)=46.67
        var result = CurveEngine.EvaluateGraph(graph, 50f);
        Assert.NotNull(result);
        Assert.InRange(result!.Value, 46.0, 47.0);
    }
}
