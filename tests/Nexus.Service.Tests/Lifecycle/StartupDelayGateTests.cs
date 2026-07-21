using System;
using Nexus.Service.Lifecycle;
using Xunit;

namespace Nexus.Service.Tests.Lifecycle;

/// <summary>
/// The gate holds process-wide state armed once at startup, so every case here
/// must leave it disabled - arming a real window would leak a delay into
/// whatever else the parallel suite runs.
/// </summary>
public sealed class StartupDelayGateTests
{
    private static readonly TimeSpan AtBoot = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan LongAfterBoot = TimeSpan.FromHours(6);

    [Fact]
    public void Unarmed_gate_is_already_elapsed()
    {
        Assert.True(StartupDelayGate.WaitAsync().IsCompleted);
        Assert.Equal(TimeSpan.Zero, StartupDelayGate.Configured);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_non_positive_window_stays_disabled(int seconds)
    {
        StartupDelayGate.ArmForBootStart(seconds, AtBoot);

        Assert.True(StartupDelayGate.WaitAsync().IsCompleted);
        Assert.Equal(TimeSpan.Zero, StartupDelayGate.Configured);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(60)]
    [InlineData(int.MaxValue)]
    public void A_start_long_after_boot_never_waits(int seconds)
    {
        StartupDelayGate.ArmForBootStart(seconds, LongAfterBoot);

        Assert.True(StartupDelayGate.WaitAsync().IsCompleted);
        Assert.Equal(TimeSpan.Zero, StartupDelayGate.Configured);
    }

    [Fact]
    public void Arming_disabled_clears_a_previous_window()
    {
        StartupDelayGate.ArmForBootStart(30, AtBoot);
        Assert.Equal(TimeSpan.FromSeconds(30), StartupDelayGate.Configured);

        StartupDelayGate.ArmForBootStart(0, AtBoot);

        Assert.True(StartupDelayGate.WaitAsync().IsCompleted);
        Assert.Equal(TimeSpan.Zero, StartupDelayGate.Configured);
    }

    [Fact]
    public void A_boot_start_clamps_the_window_to_the_maximum()
    {
        try
        {
            StartupDelayGate.ArmForBootStart(int.MaxValue, AtBoot);

            Assert.Equal(TimeSpan.FromSeconds(StartupDelayGate.MaxSeconds), StartupDelayGate.Configured);
            Assert.False(StartupDelayGate.WaitAsync().IsCompleted);
        }
        finally
        {
            StartupDelayGate.ArmForBootStart(0, AtBoot);
        }
    }
}
