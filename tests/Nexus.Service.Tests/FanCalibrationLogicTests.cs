using System.Collections.Generic;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Xunit;

namespace Nexus.Service.Tests;

public class FanCalibrationLogicTests
{
    [Fact]
    public void IsStable_DetectsLowVariance()
    {
        var samples = new[] { 1200, 1205, 1198, 1202, 1200, 1203 };
        Assert.True(FanCalibrationLogic.IsStable(samples));
    }

    [Fact]
    public void IsStable_RejectsHighVariance()
    {
        var samples = new[] { 600, 900, 1100, 1400, 1600, 1800 };
        Assert.False(FanCalibrationLogic.IsStable(samples));
    }

    [Fact]
    public void IsStable_AllowsAbsoluteFloor()
    {
        // 30 RPM mean with ±10 jitter = 33% relative stddev, but absolute is <20
        var samples = new[] { 25, 35, 28, 32, 30, 27 };
        Assert.True(FanCalibrationLogic.IsStable(samples));
    }

    [Fact]
    public void Classify_Controllable_FullRange()
    {
        var curve = MakeCurve(1890, 1700, 1500, 1300, 1100, 900, 700, 500, 350, 200, 0);
        var result = FanCalibrationLogic.Classify("fan/0", curve);
        Assert.Equal("Stalling", result.Classification); // has 0 RPM at bottom
        Assert.Equal(1890, result.MaxRpm);
        Assert.Equal(200, result.MinRpm);
        Assert.Equal(10, result.MinDuty); // duty=10 is the lowest non-zero
    }

    [Fact]
    public void Classify_Controllable_NoStall()
    {
        var curve = MakeCurve(1890, 1700, 1500, 1300, 1100, 900, 700, 500, 400, 350, 300);
        var result = FanCalibrationLogic.Classify("fan/0", curve);
        Assert.Equal("Controllable", result.Classification);
        Assert.Equal(1890, result.MaxRpm);
        Assert.Equal(300, result.MinRpm);
        Assert.Equal(0, result.MinDuty);
    }

    [Fact]
    public void Classify_Fixed_FlatResponse()
    {
        var curve = MakeCurve(1200, 1210, 1195, 1200, 1205, 1190, 1200, 1210, 1195, 1200, 1205);
        var result = FanCalibrationLogic.Classify("fan/0", curve);
        Assert.Equal("Fixed", result.Classification);
    }

    [Fact]
    public void Classify_Stalling_ZerosInRange()
    {
        var curve = MakeCurve(1500, 1300, 1100, 900, 700, 500, 300, 0, 0, 0, 0);
        var result = FanCalibrationLogic.Classify("fan/0", curve);
        Assert.Equal("Stalling", result.Classification);
        Assert.Equal(300, result.MinRpm);
        Assert.Equal(40, result.MinDuty); // duty 40 = step index 6 (100-6*10=40)
    }

    [Fact]
    public void Classify_Unresponsive_NoRpm()
    {
        var curve = MakeCurve(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var result = FanCalibrationLogic.Classify("fan/0", curve);
        Assert.Equal("Unresponsive", result.Classification);
        Assert.Equal(0, result.MaxRpm);
        Assert.Equal(0, result.MinRpm);
    }

    private static List<FanCalibrationPoint> MakeCurve(params int[] rpms)
    {
        var curve = new List<FanCalibrationPoint>(rpms.Length);
        for (int i = 0; i < rpms.Length; i++)
            curve.Add(new FanCalibrationPoint { Duty = 100 - i * 10, Rpm = rpms[i] });
        return curve;
    }
}
