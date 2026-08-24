using System.Collections.Generic;
using Nexus.Service.Cooling;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

/// <summary>
/// Trigger, Sync, Auto and Mixed evaluation. The three new modes were ported
/// from FanControl's own implementations so an imported curve behaves the same
/// here; these pin the behaviour that port has to keep.
/// </summary>
public class CurveModeTests
{
    // ── Mixed ─────────────────────────────────────────────────────────────

    private static Dictionary<string, double> Raw(params (string Id, double Value)[] entries)
    {
        var d = new Dictionary<string, double>();
        foreach (var (id, value) in entries) d[id] = value;
        return d;
    }

    [Fact]
    public void Mix_Null_ReturnsNull() =>
        Assert.Null(CurveEngine.EvaluateMix(null, Raw()));

    [Fact]
    public void Mix_NoMembers_ReturnsNull() =>
        Assert.Null(CurveEngine.EvaluateMix(new MixedCurveData(), Raw(("a", 50))));

    [Fact]
    public void Mix_MembersAllMissing_ReturnsNull()
    {
        var mixed = new MixedCurveData { CurveIds = { "gone" }, Fn = "max" };
        Assert.Null(CurveEngine.EvaluateMix(mixed, Raw(("a", 50))));
    }

    [Theory]
    [InlineData("max", 70)]
    [InlineData("min", 20)]
    [InlineData("avg", 45)]
    [InlineData("sum", 90)]
    [InlineData("subtract", 0)]
    public void Mix_AppliesFunction(string fn, double expected)
    {
        var mixed = new MixedCurveData { CurveIds = { "a", "b" }, Fn = fn };
        Assert.Equal(expected, CurveEngine.EvaluateMix(mixed, Raw(("a", 20), ("b", 70))));
    }

    [Fact]
    public void Mix_UnknownFunction_FallsBackToMax()
    {
        var mixed = new MixedCurveData { CurveIds = { "a", "b" }, Fn = "bogus" };
        Assert.Equal(70, CurveEngine.EvaluateMix(mixed, Raw(("a", 20), ("b", 70))));
    }

    [Fact]
    public void Mix_SumClampsToFullDuty()
    {
        var mixed = new MixedCurveData { CurveIds = { "a", "b" }, Fn = "sum" };
        Assert.Equal(100, CurveEngine.EvaluateMix(mixed, Raw(("a", 80), ("b", 70))));
    }

    [Fact]
    public void Mix_SkipsMembersThatDidNotEvaluate()
    {
        var mixed = new MixedCurveData { CurveIds = { "a", "missing" }, Fn = "max" };
        Assert.Equal(20, CurveEngine.EvaluateMix(mixed, Raw(("a", 20))));
    }

    // ── Sync ──────────────────────────────────────────────────────────────

    [Fact]
    public void Sync_Null_ReturnsNull() =>
        Assert.Null(CurveEngine.EvaluateSync(null, Raw(("fan", 50))));

    [Fact]
    public void Sync_UnknownChannel_ReturnsNull()
    {
        var sync = new SyncCurveData { SourceChannelId = "absent" };
        Assert.Null(CurveEngine.EvaluateSync(sync, Raw(("fan", 50))));
    }

    [Fact]
    public void Sync_EmptySource_ReturnsNull() =>
        Assert.Null(CurveEngine.EvaluateSync(new SyncCurveData(), Raw(("fan", 50))));

    [Fact]
    public void Sync_MirrorsSourceDuty()
    {
        var sync = new SyncCurveData { SourceChannelId = "fan" };
        Assert.Equal(50, CurveEngine.EvaluateSync(sync, Raw(("fan", 50))));
    }

    [Fact]
    public void Sync_AbsoluteOffset_AddsPoints()
    {
        var sync = new SyncCurveData { SourceChannelId = "fan", Offset = 10 };
        Assert.Equal(60, CurveEngine.EvaluateSync(sync, Raw(("fan", 50))));
    }

    [Fact]
    public void Sync_NegativeOffset_Subtracts()
    {
        var sync = new SyncCurveData { SourceChannelId = "fan", Offset = -15 };
        Assert.Equal(35, CurveEngine.EvaluateSync(sync, Raw(("fan", 50))));
    }

    [Fact]
    public void Sync_Proportional_ScalesByPercent()
    {
        var sync = new SyncCurveData { SourceChannelId = "fan", Offset = 20, Proportional = true };
        Assert.Equal(60, CurveEngine.EvaluateSync(sync, Raw(("fan", 50))));
    }

    // ── Trigger ───────────────────────────────────────────────────────────

    private static TriggerCurveData Trigger() => new()
    {
        IdleTemp = 40,
        LoadTemp = 60,
        IdleSpeed = 30,
        LoadSpeed = 80,
    };

    [Fact]
    public void Trigger_FirstEvaluationBelowLoad_StartsIdle()
    {
        var state = new TriggerCurveState();
        Assert.Equal(30, state.Evaluate(Trigger(), 50, null, 1));
    }

    [Fact]
    public void Trigger_FirstEvaluationAboveLoad_StartsLoad()
    {
        var state = new TriggerCurveState();
        Assert.Equal(80, state.Evaluate(Trigger(), 65, null, 1));
    }

    [Fact]
    public void Trigger_HoldsBetweenThresholds()
    {
        var state = new TriggerCurveState();
        var cfg = Trigger();
        state.Evaluate(cfg, 50, null, 1);
        // 50 C sits in the dead zone: the previous command must survive it.
        Assert.Equal(30, state.Evaluate(cfg, 50, 30, 1));
        Assert.Equal(80, state.Evaluate(cfg, 50, 80, 1));
    }

    [Fact]
    public void Trigger_CrossingUp_WaitsForResponseTicks()
    {
        var state = new TriggerCurveState();
        var cfg = Trigger();
        state.Evaluate(cfg, 50, null, 3);
        Assert.Equal(30, state.Evaluate(cfg, 65, 30, 3));
        Assert.Equal(30, state.Evaluate(cfg, 65, 30, 3));
        Assert.Equal(80, state.Evaluate(cfg, 65, 30, 3));
    }

    [Fact]
    public void Trigger_CrossingDown_WaitsForResponseTicks()
    {
        var state = new TriggerCurveState();
        var cfg = Trigger();
        state.Evaluate(cfg, 65, null, 2);
        // Settle in the dead zone so the debounce counter starts clean.
        state.Evaluate(cfg, 50, 80, 2);
        Assert.Equal(80, state.Evaluate(cfg, 35, 80, 2));
        Assert.Equal(30, state.Evaluate(cfg, 35, 80, 2));
    }

    [Fact]
    public void Trigger_DebounceCounterIsSharedAcrossDirections()
    {
        var state = new TriggerCurveState();
        var cfg = Trigger();
        state.Evaluate(cfg, 65, null, 2);
        // One tick of up-crossing debounce, then straight to a down-crossing:
        // FanControl carries the same counter across, so the second tick fires.
        Assert.Equal(80, state.Evaluate(cfg, 65, 80, 2));
        Assert.Equal(30, state.Evaluate(cfg, 35, 80, 2));
    }

    [Fact]
    public void Trigger_BriefSpikeDoesNotFlip()
    {
        var state = new TriggerCurveState();
        var cfg = Trigger();
        state.Evaluate(cfg, 50, null, 3);
        Assert.Equal(30, state.Evaluate(cfg, 65, 30, 3));
        // Back into the dead zone before the debounce elapsed: the counter
        // resets, so a later crossing starts over.
        Assert.Equal(30, state.Evaluate(cfg, 50, 30, 3));
        Assert.Equal(30, state.Evaluate(cfg, 65, 30, 3));
        Assert.Equal(30, state.Evaluate(cfg, 65, 30, 3));
        Assert.Equal(80, state.Evaluate(cfg, 65, 30, 3));
    }

    [Fact]
    public void Trigger_StaysLatchedHighWhileHot()
    {
        var state = new TriggerCurveState();
        var cfg = Trigger();
        state.Evaluate(cfg, 65, null, 1);
        Assert.Equal(80, state.Evaluate(cfg, 65, 80, 1));
        Assert.Equal(80, state.Evaluate(cfg, 90, 80, 1));
    }

    // ── Auto ──────────────────────────────────────────────────────────────

    private static AutoCurveData Auto() => new()
    {
        IdleTemp = 40,
        LoadTemp = 70,
        MinSpeed = 20,
        MaxSpeed = 100,
        Step = 5,
        Deadband = 2,
        ResponseTime = 2,
    };

    [Fact]
    public void Auto_FirstEvaluationBelowLoad_StartsAtMin()
    {
        var state = new AutoCurveState();
        Assert.Equal(20, state.Evaluate(Auto(), 45, null, 2));
    }

    [Fact]
    public void Auto_FirstEvaluationAboveLoad_StartsAtMax()
    {
        var state = new AutoCurveState();
        Assert.Equal(100, state.Evaluate(Auto(), 80, null, 2));
    }

    [Fact]
    public void Auto_AtOrBelowIdle_ReturnsMinSpeed()
    {
        var state = new AutoCurveState();
        var cfg = Auto();
        state.Evaluate(cfg, 45, null, 2);
        Assert.Equal(20, state.Evaluate(cfg, 40, 60, 2));
        Assert.Equal(20, state.Evaluate(cfg, 30, 60, 2));
    }

    [Fact]
    public void Auto_BetweenIdleAndLoad_RampsLinearly()
    {
        var state = new AutoCurveState();
        var cfg = Auto();
        state.Evaluate(cfg, 45, null, 2);
        // Midway between idle (40) and load (70) is midway between min and max.
        var result = state.Evaluate(cfg, 55, 20, 2);
        Assert.Equal(60, result!.Value, 3);
    }

    [Fact]
    public void Auto_UnderLoadAndRising_StepsUpAfterDebounce()
    {
        var state = new AutoCurveState();
        var cfg = Auto();
        state.Evaluate(cfg, 80, null, 1);
        var command = 50.0;
        // Above load temp with a 1-tick debounce: every tick adds one Step.
        for (var i = 0; i < 3; i++)
        {
            command = state.Evaluate(cfg, 80, command, 1)!.Value;
        }
        Assert.Equal(65, command);
    }

    [Fact]
    public void Auto_StepUpClampsAtMaxSpeed()
    {
        var state = new AutoCurveState();
        var cfg = Auto();
        state.Evaluate(cfg, 80, null, 1);
        var command = 98.0;
        for (var i = 0; i < 5; i++)
        {
            command = state.Evaluate(cfg, 80, command, 1)!.Value;
        }
        Assert.Equal(100, command);
    }

    [Fact]
    public void Sync_FollowingItsOwnOutput_ReturnsNull()
    {
        var sync = new SyncCurveData { SourceChannelId = "fan", Offset = 10, Proportional = true };
        var outputs = new List<CurveOutputDocument> { new() { Id = "fan", Type = "Fan" } };
        Assert.Null(CurveEngine.EvaluateSync(sync, Raw(("fan", 50)), outputs));
    }

    // ── Ordering ──────────────────────────────────────────────────────────

    private static CurveDocument Doc(string id, string type) => new()
    {
        Id = id,
        Name = id,
        Type = type,
        Input = new CurveInputDocument(),
    };

    [Fact]
    public void Ordering_PutsMixAfterItsMembers()
    {
        var mix = Doc("mix", "Mixed");
        mix.Mixed = new MixedCurveData { CurveIds = { "a" } };
        var a = Doc("a", "Flat");
        var ordered = CurveOrdering.Sort(new[] { mix, a });
        Assert.Equal(new[] { "a", "mix" }, ordered.ConvertAll(c => c.Id));
    }

    [Fact]
    public void Ordering_PutsSyncAfterTheCurveDrivingItsSource()
    {
        var sync = Doc("sync", "Sync");
        sync.Sync = new SyncCurveData { SourceChannelId = "fan" };
        var driver = Doc("driver", "Flat");
        driver.Outputs.Add(new CurveOutputDocument { Id = "fan", Type = "Fan" });
        var ordered = CurveOrdering.Sort(new[] { sync, driver });
        Assert.Equal(new[] { "driver", "sync" }, ordered.ConvertAll(c => c.Id));
    }

    [Fact]
    public void Ordering_KeepsInputOrderForIndependentCurves()
    {
        var ordered = CurveOrdering.Sort(new[] { Doc("a", "Flat"), Doc("b", "Flat"), Doc("c", "Flat") });
        Assert.Equal(new[] { "a", "b", "c" }, ordered.ConvertAll(c => c.Id));
    }

    [Fact]
    public void Ordering_DanglingMemberIdIsNotADependency()
    {
        var mix = Doc("mix", "Mixed");
        mix.Mixed = new MixedCurveData { CurveIds = { "deleted" } };
        Assert.Empty(CurveOrdering.FindCycleMembers(new[] { mix }));
    }

    [Fact]
    public void Ordering_NoCycle_ReportsNone()
    {
        var mix = Doc("mix", "Mixed");
        mix.Mixed = new MixedCurveData { CurveIds = { "a" } };
        Assert.Empty(CurveOrdering.FindCycleMembers(new[] { mix, Doc("a", "Flat") }));
    }

    [Fact]
    public void Ordering_SelfReferenceIsNotACycle()
    {
        // A Mix listing itself just drops that member rather than deadlocking.
        var mix = Doc("mix", "Mixed");
        mix.Mixed = new MixedCurveData { CurveIds = { "mix" } };
        Assert.Empty(CurveOrdering.FindCycleMembers(new[] { mix }));
    }

    [Fact]
    public void Ordering_MutualMixReferenceIsACycle()
    {
        var a = Doc("a", "Mixed");
        a.Mixed = new MixedCurveData { CurveIds = { "b" } };
        var b = Doc("b", "Mixed");
        b.Mixed = new MixedCurveData { CurveIds = { "a" } };
        var cyclic = CurveOrdering.FindCycleMembers(new[] { a, b });
        Assert.NotEmpty(cyclic);
    }

    [Fact]
    public void Ordering_SyncFollowingItsOwnOutputIsACycle()
    {
        var sync = Doc("sync", "Sync");
        sync.Sync = new SyncCurveData { SourceChannelId = "fan" };
        sync.Outputs.Add(new CurveOutputDocument { Id = "fan", Type = "Fan" });
        Assert.Equal(new[] { "sync" }, CurveOrdering.FindCycleMembers(new[] { sync }));
    }

    [Fact]
    public void Ordering_SyncCycleThroughChannelsIsACycle()
    {
        var s1 = Doc("s1", "Sync");
        s1.Sync = new SyncCurveData { SourceChannelId = "fan-b" };
        s1.Outputs.Add(new CurveOutputDocument { Id = "fan-a", Type = "Fan" });
        var s2 = Doc("s2", "Sync");
        s2.Sync = new SyncCurveData { SourceChannelId = "fan-a" };
        s2.Outputs.Add(new CurveOutputDocument { Id = "fan-b", Type = "Fan" });
        Assert.NotEmpty(CurveOrdering.FindCycleMembers(new[] { s1, s2 }));
    }

    [Fact]
    public void Auto_ReversedSpeedBounds_DoNotThrow()
    {
        // Math.Clamp throws when its bounds are reversed, and the throw would
        // come from inside the engine tick and abort every other curve with it.
        var state = new AutoCurveState();
        var cfg = new AutoCurveData { IdleTemp = 40, LoadTemp = 70, MinSpeed = 80, MaxSpeed = 20, Step = 5, Deadband = 2 };
        state.Evaluate(cfg, 90, null, 1);
        var result = state.Evaluate(cfg, 90, 50, 1);
        Assert.NotNull(result);
        Assert.InRange(result!.Value, 20, 80);
    }

    [Fact]
    public void Auto_AdoptsThePreviousCommandWhenItHasNoLoadTargetYet()
    {
        // A curve that changed type into Auto arrives with the engine's stored
        // output but no load target; it must drive, not fall silent.
        var state = new AutoCurveState();
        var cfg = Auto();
        Assert.NotNull(state.Evaluate(cfg, 80, 55, 1));
    }

    [Fact]
    public void Auto_ResetClearsLoadLatch()
    {
        var state = new AutoCurveState();
        var cfg = Auto();
        state.Evaluate(cfg, 80, null, 1);
        state.Evaluate(cfg, 80, 50, 1);
        state.Reset();
        // After a reset the next call is a first evaluation again.
        Assert.Equal(20, state.Evaluate(cfg, 45, null, 1));
    }
}
