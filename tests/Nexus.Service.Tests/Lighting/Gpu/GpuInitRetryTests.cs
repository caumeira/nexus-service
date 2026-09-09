using System;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

public class GpuInitRetryTests
{
    [Fact]
    public void TheScheduleBacksOff()
    {
        var first = GpuInitRetry.DelayFor(1);
        var second = GpuInitRetry.DelayFor(2);
        var third = GpuInitRetry.DelayFor(3);

        Assert.True(first < second);
        Assert.True(second < third);
    }

    [Fact]
    public void TheBudgetIsBounded()
    {
        Assert.InRange(GpuInitRetry.MaxAttempts, 1, 5);
    }

    [Fact]
    public void AttemptsPastTheBudgetReuseTheLongestDelay()
    {
        Assert.Equal(GpuInitRetry.DelayFor(3), GpuInitRetry.DelayFor(99));
    }

    [Fact]
    public void TheInSessionRearmBudgetIsBounded()
    {
        Assert.InRange(GpuInitRetry.MaxLatchRearms, 1, 32);
    }

    [Fact]
    public void EveryDelayIsPositive()
    {
        for (var i = 0; i <= GpuInitRetry.MaxAttempts + 1; i++)
        {
            Assert.True(GpuInitRetry.DelayFor(i) > TimeSpan.Zero);
        }
    }
}
