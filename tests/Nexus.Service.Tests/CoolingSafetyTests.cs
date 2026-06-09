using System.Collections.Generic;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Xunit;

namespace Nexus.Service.Tests;

public class CoolingSafetyTests
{
    [Theory]
    [InlineData(50, 50)]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    [InlineData(500, 100)]
    [InlineData(-1, 0)]
    [InlineData(-9999, 0)]
    public void ClampDuty_int_bounds_to_0_100(int input, int expected)
        => Assert.Equal(expected, CoolingSafety.ClampDuty(input));

    [Theory]
    [InlineData(50.0, 50.0)]
    [InlineData(150.0, 100.0)]
    [InlineData(-3.0, 0.0)]
    public void ClampDuty_double_bounds_to_0_100(double input, double expected)
        => Assert.Equal(expected, CoolingSafety.ClampDuty(input));

    [Fact]
    public void Sanitize_clamps_every_curve_speed_kind()
    {
        var body = new SetCurvesBody
        {
            GlobalSpeedModifier = 1000.0,
            Curves = new List<Curve>
            {
                new() { Type = "Flat", Flat = new FlatCurve { Speed = 500 } },
                new() { Type = "Linear", Linear = new LinearCurve { MinSpeed = -10, MaxSpeed = 250 } },
                new()
                {
                    Type = "Graph",
                    Graph = new GraphCurve
                    {
                        Points = new List<GraphPoint>
                        {
                            new() { Temp = 30, Speed = -5 },
                            new() { Temp = 80, Speed = 999 },
                        },
                    },
                },
            },
        };

        CoolingSafety.Sanitize(body);

        Assert.Equal(CoolingSafety.MaxGlobalModifier, body.GlobalSpeedModifier);
        Assert.Equal(100, body.Curves[0].Flat!.Speed);
        Assert.Equal(0.0, body.Curves[1].Linear!.MinSpeed);
        Assert.Equal(100.0, body.Curves[1].Linear!.MaxSpeed);
        Assert.Equal(0.0, body.Curves[2].Graph!.Points[0].Speed);
        Assert.Equal(100.0, body.Curves[2].Graph!.Points[1].Speed);
    }

    [Fact]
    public void Sanitize_leaves_valid_curves_untouched()
    {
        var body = new SetCurvesBody
        {
            GlobalSpeedModifier = 1.0,
            Curves = new List<Curve>
            {
                new() { Type = "Linear", Linear = new LinearCurve { MinSpeed = 20, MaxSpeed = 90 } },
            },
        };

        CoolingSafety.Sanitize(body);

        Assert.Equal(1.0, body.GlobalSpeedModifier);
        Assert.Equal(20.0, body.Curves[0].Linear!.MinSpeed);
        Assert.Equal(90.0, body.Curves[0].Linear!.MaxSpeed);
    }

    [Fact]
    public void Sanitize_recovers_a_non_finite_global_modifier()
    {
        var body = new SetCurvesBody { GlobalSpeedModifier = double.NaN };
        CoolingSafety.Sanitize(body);
        Assert.Equal(1.0, body.GlobalSpeedModifier);
    }

    [Fact]
    public void Sanitize_collapses_non_finite_curve_speeds_to_zero()
    {
        var body = new SetCurvesBody
        {
            Curves = new List<Curve>
            {
                new() { Type = "Flat", Flat = new FlatCurve { Speed = 50 } },
                new() { Type = "Linear", Linear = new LinearCurve { MinSpeed = double.NaN, MaxSpeed = double.PositiveInfinity } },
                new()
                {
                    Type = "Graph",
                    Graph = new GraphCurve
                    {
                        SpeedModifier = 1000.0,
                        Points = new List<GraphPoint> { new() { Temp = 40, Speed = double.NaN } },
                    },
                },
            },
        };

        CoolingSafety.Sanitize(body);

        Assert.Equal(0.0, body.Curves[1].Linear!.MinSpeed);          // NaN -> floor
        Assert.Equal(0.0, body.Curves[1].Linear!.MaxSpeed);          // +Inf -> floor
        Assert.Equal(0.0, body.Curves[2].Graph!.Points[0].Speed);    // NaN point -> floor
        Assert.Equal(CoolingSafety.MaxGlobalModifier, body.Curves[2].Graph!.SpeedModifier); // 1000 -> ceiling
    }
}
