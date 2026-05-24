using Nexus.Service.Fps;

namespace Nexus.Service.Tests;

public class FpsCalculatorTests
{
    [Fact]
    public void AddFrameTicks_ComputesStableFpsFromPresentIntervals()
    {
        var calculator = new FpsCalculator();
        var start = DateTime.UtcNow.Ticks;
        var frameTicks = TimeSpan.TicksPerSecond / 60;
        double fps = 0;

        for (var i = 0; i < 60; i++)
        {
            fps = calculator.AddFrameTicks(start + i * frameTicks);
        }

        Assert.InRange(fps, 59.9, 60.1);
    }

    [Fact]
    public void Reset_ClearsSampleWindow()
    {
        var calculator = new FpsCalculator();
        var start = DateTime.UtcNow.Ticks;

        calculator.AddFrameTicks(start);
        calculator.AddFrameTicks(start + TimeSpan.TicksPerSecond / 60);
        Assert.True(calculator.FramesPerSecond > 0);

        calculator.Reset();

        Assert.Equal(0, calculator.Count);
        Assert.Equal(0, calculator.FramesPerSecond);
    }
}
