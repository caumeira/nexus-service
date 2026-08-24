using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Tick-level tests for the curve types that are not a pure function of one
/// temperature: Mixed reads other curves, Sync reads a fan channel, and both
/// depend on evaluation order. Mixed in particular was persisted and shown in
/// the UI for a long time while the engine never evaluated it, so "the curve
/// actually reaches hardware" is the assertion that matters here.
/// </summary>
public class CurveEngineModeTests
{
    private sealed class FakeFanProvider : IFanControlProvider
    {
        public readonly HashSet<string> Present = new();
        public readonly List<(string Id, int Duty)> Driven = new();
        public readonly Dictionary<string, int> Duty = new();
        public float Temperature = 50f;

        public IReadOnlyList<FanChannel> GetFanChannels() =>
            Present.Select(id => new FanChannel
            {
                Id = id,
                Name = id,
                DutyPercent = Duty.TryGetValue(id, out var d) ? d : 0,
            }).ToList();

        public IReadOnlyList<TemperatureSource> GetTemperatureSources() =>
            Array.Empty<TemperatureSource>();

        public float? ReadTemperature(string sensorId) => Temperature;

        public int SetFanSpeed(string channelId, int dutyPercent)
        {
            Duty[channelId] = dutyPercent;
            return dutyPercent;
        }

        public void DriveFanSpeed(string channelId, int dutyPercent)
        {
            Driven.Add((channelId, dutyPercent));
            Duty[channelId] = dutyPercent;
        }

        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }

        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    private static (CurveEngine Engine, FakeFanProvider Fans, InMemoryConfigStore Store) Build()
    {
        var fans = new FakeFanProvider();
        var store = new InMemoryConfigStore();
        return (new CurveEngine(fans, store, new MultiplexHub()), fans, store);
    }

    private static CurveDocument Flat(string id, string outputId, int speed) => new()
    {
        Id = id,
        Name = id,
        Type = "Flat",
        Input = new CurveInputDocument { Id = "t", Type = "Temperature" },
        Outputs = { new CurveOutputDocument { Id = outputId, Type = "Fan" } },
        Flat = new FlatCurveData { Speed = speed },
    };

    private static CurveDocument Sourceless(string id, string type, string outputId) => new()
    {
        Id = id,
        Name = id,
        Type = type,
        Input = new CurveInputDocument(),
        Outputs = { new CurveOutputDocument { Id = outputId, Type = "Fan" } },
    };

    [Fact]
    public void Mixed_DrivesItsOutput()
    {
        var (engine, fans, store) = Build();
        var mix = Sourceless("mix", "Mixed", "out");
        mix.Mixed = new MixedCurveData { CurveIds = { "a", "b" }, Fn = "max" };
        store.Update(s =>
        {
            s.Cooling.Curves.Add(Flat("a", "fan-a", 30));
            s.Cooling.Curves.Add(Flat("b", "fan-b", 70));
            s.Cooling.Curves.Add(mix);
        });
        fans.Present.UnionWith(new[] { "fan-a", "fan-b", "out" });

        engine.Tick();

        Assert.Contains(("out", 70), fans.Driven);
    }

    [Fact]
    public void Mixed_EvaluatesEvenWhenListedBeforeItsMembers()
    {
        var (engine, fans, store) = Build();
        var mix = Sourceless("mix", "Mixed", "out");
        mix.Mixed = new MixedCurveData { CurveIds = { "a" }, Fn = "max" };
        store.Update(s =>
        {
            s.Cooling.Curves.Add(mix);
            s.Cooling.Curves.Add(Flat("a", "fan-a", 45));
        });
        fans.Present.UnionWith(new[] { "fan-a", "out" });

        engine.Tick();

        Assert.Contains(("out", 45), fans.Driven);
    }

    [Fact]
    public void Sync_FollowsAChannelDrivenByAnotherCurveInTheSameTick()
    {
        var (engine, fans, store) = Build();
        var sync = Sourceless("sync", "Sync", "out");
        sync.Sync = new SyncCurveData { SourceChannelId = "fan-a", Offset = 10 };
        store.Update(s =>
        {
            // Sync listed first: the ordering pass has to move it after "a".
            s.Cooling.Curves.Add(sync);
            s.Cooling.Curves.Add(Flat("a", "fan-a", 40));
        });
        fans.Present.UnionWith(new[] { "fan-a", "out" });

        engine.Tick();

        Assert.Contains(("out", 50), fans.Driven);
    }

    [Fact]
    public void Sync_FollowsAManualChannelsLiveDuty()
    {
        var (engine, fans, store) = Build();
        var sync = Sourceless("sync", "Sync", "out");
        sync.Sync = new SyncCurveData { SourceChannelId = "fan-a" };
        store.Update(s => s.Cooling.Curves.Add(sync));
        fans.Present.UnionWith(new[] { "fan-a", "out" });
        fans.Duty["fan-a"] = 62;

        engine.Tick();

        Assert.Contains(("out", 62), fans.Driven);
    }

    [Fact]
    public void Sync_AbsentSourceChannel_DrivesNothing()
    {
        var (engine, fans, store) = Build();
        var sync = Sourceless("sync", "Sync", "out");
        sync.Sync = new SyncCurveData { SourceChannelId = "gone" };
        store.Update(s => s.Cooling.Curves.Add(sync));
        fans.Present.Add("out");

        engine.Tick();

        Assert.Empty(fans.Driven);
    }

    [Fact]
    public void Sync_DoesNotCompoundTheGlobalModifier()
    {
        var (engine, fans, store) = Build();
        var sync = Sourceless("sync", "Sync", "out");
        sync.Sync = new SyncCurveData { SourceChannelId = "fan-a" };
        store.Update(s =>
        {
            s.Cooling.GlobalSpeedModifier = 1.5;
            s.Cooling.Curves.Add(Flat("a", "fan-a", 40));
            s.Cooling.Curves.Add(sync);
        });
        fans.Present.UnionWith(new[] { "fan-a", "out" });

        engine.Tick();

        // The source already carries the boost (40 * 1.5); the mirror must not
        // apply it a second time.
        Assert.Contains(("fan-a", 60), fans.Driven);
        Assert.Contains(("out", 60), fans.Driven);
    }

    [Fact]
    public void Trigger_DrivesItsOutput()
    {
        var (engine, fans, store) = Build();
        var trigger = new CurveDocument
        {
            Id = "trig",
            Name = "trig",
            Type = "Trigger",
            Input = new CurveInputDocument { Id = "t", Type = "Temperature" },
            Outputs = { new CurveOutputDocument { Id = "out", Type = "Fan" } },
            Trigger = new TriggerCurveData { IdleTemp = 40, LoadTemp = 60, IdleSpeed = 25, LoadSpeed = 85 },
        };
        store.Update(s => s.Cooling.Curves.Add(trigger));
        fans.Present.Add("out");
        fans.Temperature = 70f;

        engine.Tick();

        Assert.Contains(("out", 85), fans.Driven);
    }

    [Fact]
    public void Auto_DrivesItsOutput()
    {
        var (engine, fans, store) = Build();
        var auto = new CurveDocument
        {
            Id = "auto",
            Name = "auto",
            Type = "Auto",
            Input = new CurveInputDocument { Id = "t", Type = "Temperature" },
            Outputs = { new CurveOutputDocument { Id = "out", Type = "Fan" } },
            Auto = new AutoCurveData { IdleTemp = 40, LoadTemp = 70, MinSpeed = 20, MaxSpeed = 100, Step = 5, Deadband = 2 },
        };
        store.Update(s => s.Cooling.Curves.Add(auto));
        fans.Present.Add("out");
        fans.Temperature = 30f;

        engine.Tick();

        Assert.Contains(("out", 20), fans.Driven);
    }

    [Fact]
    public void FanOffset_ShiftsTheAppliedDuty()
    {
        var (engine, fans, store) = Build();
        store.Update(s =>
        {
            s.Cooling.Curves.Add(Flat("a", "fan-a", 40));
            s.Cooling.FanOffsets["fan-a"] = 15;
        });
        fans.Present.Add("fan-a");

        engine.Tick();

        Assert.Contains(("fan-a", 55), fans.Driven);
    }

    [Fact]
    public void FanOffset_ClampsAtFullDuty()
    {
        var (engine, fans, store) = Build();
        store.Update(s =>
        {
            s.Cooling.Curves.Add(Flat("a", "fan-a", 95));
            s.Cooling.FanOffsets["fan-a"] = 20;
        });
        fans.Present.Add("fan-a");

        engine.Tick();

        Assert.Contains(("fan-a", 100), fans.Driven);
    }

    [Fact]
    public void DependencyCycle_DoesNotHangOrThrow()
    {
        var (engine, fans, store) = Build();
        // Two Sync curves each following the channel the other drives.
        var s1 = Sourceless("s1", "Sync", "fan-a");
        s1.Sync = new SyncCurveData { SourceChannelId = "fan-b" };
        var s2 = Sourceless("s2", "Sync", "fan-b");
        s2.Sync = new SyncCurveData { SourceChannelId = "fan-a" };
        store.Update(s =>
        {
            s.Cooling.Curves.Add(s1);
            s.Cooling.Curves.Add(s2);
        });
        fans.Present.UnionWith(new[] { "fan-a", "fan-b" });
        fans.Duty["fan-a"] = 30;
        fans.Duty["fan-b"] = 30;

        engine.Tick();
        engine.Tick();

        // Both still evaluate against the last known duty rather than dropping out.
        Assert.Contains(fans.Driven, d => d.Id == "fan-a");
        Assert.Contains(fans.Driven, d => d.Id == "fan-b");
    }
}
