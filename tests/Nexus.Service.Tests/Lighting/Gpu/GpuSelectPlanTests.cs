using System;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

/// <summary>
/// The boot decision on a single-GPU box. The row that matters is a persisted
/// "off" with no crash guard: the branch it belongs to used to return before it
/// ever read the persisted state, so an off latch could not hold and the box
/// re-probed, re-crashed and restarted every few seconds.
/// </summary>
public class GpuSelectPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static GpuSelectDecision Single(string? state, string? guard) =>
        GpuSelectPlan.NextAction(state, guard, usableAdapters: 1, Now);

    [Fact]
    public void SoleGpu_Unprobed_Probes()
    {
        Assert.Equal(GpuSelectAction.Probe, Single("unprobed", null).Action);
    }

    [Fact]
    public void SoleGpu_CrashGuard_LatchesOffWithoutProbing()
    {
        Assert.Equal(GpuSelectAction.DeclineAndLatch, Single("unprobed", "integrated").Action);
    }

    [Fact]
    public void SoleGpu_PersistedOff_DoesNotProbe()
    {
        var decision = Single("off", null);

        Assert.Equal(GpuSelectAction.DeclineNoProbe, decision.Action);
        // A bare token carries no timestamp, so the caller stamps one and the
        // re-probe clock starts.
        Assert.True(decision.RestampLatch);
    }

    [Fact]
    public void SoleGpu_FreshLatch_HoldsUntilItExpires()
    {
        var latched = new GpuSelectState(GpuSelectState.Off, Now.AddMinutes(-5), 1);

        Assert.Equal(GpuSelectAction.DeclineNoProbe,
            GpuSelectPlan.NextAction(latched.Format(), null, 1, Now).Action);
    }

    [Fact]
    public void SoleGpu_ExpiredLatch_ProbesAgain()
    {
        var latched = new GpuSelectState(GpuSelectState.Off, Now.AddHours(-2), 1);

        var decision = GpuSelectPlan.NextAction(latched.Format(), null, 1, Now);

        Assert.Equal(GpuSelectAction.Probe, decision.Action);
        Assert.Contains("expired", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SoleGpu_LatchWindowIsCompressible()
    {
        var latched = new GpuSelectState(GpuSelectState.Off, Now.AddMinutes(-2), 1);

        Assert.Equal(GpuSelectAction.Probe,
            GpuSelectPlan.NextAction(latched.Format(), null, 1, Now, TimeSpan.FromMinutes(1)).Action);
    }

    [Fact]
    public void MultiGpu_RememberedCard_WarmsWithoutProbing()
    {
        var decision = GpuSelectPlan.NextAction("integrated", null, 2, Now);

        Assert.Equal(GpuSelectAction.WarmRemembered, decision.Action);
        Assert.Equal(GpuSelectState.Integrated, decision.Card);
    }

    [Fact]
    public void MultiGpu_CrashGuardOnTheRememberedCard_Demotes()
    {
        var decision = GpuSelectPlan.NextAction("integrated", "integrated", 2, Now);

        Assert.Equal(GpuSelectAction.Probe, decision.Action);
        Assert.Equal(GpuSelectState.Unprobed, decision.Card);
    }

    [Fact]
    public void MultiGpu_CrashGuardOnTheOtherCard_KeepsTheRememberedOne()
    {
        Assert.Equal(GpuSelectAction.WarmRemembered,
            GpuSelectPlan.NextAction("integrated", "discrete", 2, Now).Action);
    }

    [Fact]
    public void MultiGpu_FreshLatch_HoldsAndExpiredLatchProbes()
    {
        var held = new GpuSelectState(GpuSelectState.Off, Now.AddMinutes(-5), 1);
        var stale = new GpuSelectState(GpuSelectState.Off, Now.AddHours(-2), 1);

        Assert.Equal(GpuSelectAction.DeclineNoProbe, GpuSelectPlan.NextAction(held.Format(), null, 2, Now).Action);
        Assert.Equal(GpuSelectAction.Probe, GpuSelectPlan.NextAction(stale.Format(), null, 2, Now).Action);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void UnreadableState_Probes(string? state)
    {
        Assert.Equal(GpuSelectAction.Probe, Single(state, null).Action);
    }

    [Fact]
    public void EmptyCrashGuardFile_IsNotACrash()
    {
        Assert.Equal(GpuSelectAction.Probe, Single("unprobed", "   ").Action);
    }
}
