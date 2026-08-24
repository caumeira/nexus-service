using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Migration.FanControl;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Import a FanControl configuration and then run the curve engine over what it
/// wrote, so the whole chain is covered in one place: parse, match, apply, and
/// fans actually driven at the duty the imported curve asks for. The pieces are
/// unit-tested separately; this is the seam between them, and it is the part a
/// lab box cannot prove unless that box happens to have FanControl actively
/// driving its fans.
/// </summary>
public class FanControlImportEndToEndTests
{
    private sealed class FakeDetector : IFanControlDetector
    {
        private readonly string _path;

        public FakeDetector(string path) => _path = path;

        public FanControlDetectionResult Detect() => new(
            Detected: true, Running: false, InstallLocation: "C:\\FanControl", Version: "217",
            Configs: new List<FanControlConfigFile> { new(_path, "userConfig", 0, true) },
            AutostartPresent: false);

        public string? ReadConfig(string path) =>
            string.Equals(path, _path, StringComparison.OrdinalIgnoreCase) ? File.ReadAllText(_path) : null;

        public Task<bool> CloseAppAsync() => Task.FromResult(true);

        public bool DisableAutostart() => true;
    }

    private sealed class FakeFanProvider : IFanControlProvider
    {
        public readonly List<(string Id, int Duty)> Driven = new();
        public readonly Dictionary<string, int> Duty = new();
        public float Temperature = 50f;

        public readonly List<FanChannel> Channels = new()
        {
            new() { Id = "/lpc/nct6797d/0/control/0", Name = "Fan #1" },
            new() { Id = "/lpc/nct6797d/0/control/1", Name = "Fan #2" },
            new() { Id = "/lpc/nct6797d/0/control/2", Name = "Fan #3" },
            new() { Id = "/lpc/nct6797d/0/control/3", Name = "Fan #4" },
        };

        public IReadOnlyList<FanChannel> GetFanChannels()
        {
            foreach (var c in Channels)
            {
                c.DutyPercent = Duty.TryGetValue(c.Id, out var d) ? d : 0;
            }
            return Channels;
        }

        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => new List<TemperatureSource>
        {
            new() { Id = "/amdcpu/0/temperature/2", Name = "Core (Tctl/Tdie)", Category = "CPU", Value = 50 },
            new() { Id = "/gpu-nvidia/0/temperature/0", Name = "GPU Core", Category = "GPU", Value = 50 },
            new() { Id = "/lpc/nct6797d/0/temperature/1", Name = "Temperature #2", Category = "Motherboard", Value = 50 },
        };

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

    private static (FanControlImportService Import, FakeFanProvider Fans, InMemoryConfigStore Store) Build()
    {
        var path = Path.Combine("Fixtures", "FanControl", "all-curve-kinds.json");
        var fans = new FakeFanProvider();
        var store = new InMemoryConfigStore();
        return (new FanControlImportService(new FakeDetector(path), fans, store), fans, store);
    }

    private static readonly string[] AllCategories =
    {
        FanControlImportService.CategoryCurves,
        FanControlImportService.CategoryCalibration,
        FanControlImportService.CategoryNames,
        FanControlImportService.CategoryOffsets,
        FanControlImportService.CategoryManual,
    };

    [Fact]
    public void Apply_ThenTick_DrivesTheFansTheImportedCurvesOwn()
    {
        var (import, fans, store) = Build();
        var result = import.Apply(null, AllCategories);
        Assert.False(result.Error);

        var engine = new CurveEngine(fans, store, new MultiplexHub());
        fans.Temperature = 55f;
        engine.Tick();

        // The graph curve drives Fan #1: at 55 C its imported points put it
        // between 49.5% and 100%, and the fan must actually receive a write.
        var driven = fans.Driven.Where(d => d.Id == "/lpc/nct6797d/0/control/0").ToList();
        Assert.Single(driven);
        Assert.InRange(driven[0].Duty, 50, 60);
    }

    [Fact]
    public void Apply_ThenTick_RunsTheImportedTriggerCurve()
    {
        var (import, fans, store) = Build();
        import.Apply(null, AllCategories);

        var engine = new CurveEngine(fans, store, new MultiplexHub());
        // Above the imported curve's load threshold (65 C).
        fans.Temperature = 70f;
        engine.Tick();

        Assert.Contains(("/lpc/nct6797d/0/control/1", 80), fans.Driven);
    }

    [Fact]
    public void Apply_ThenTick_RunsTheImportedSyncCurve()
    {
        var (import, fans, store) = Build();
        import.Apply(null, AllCategories);

        var engine = new CurveEngine(fans, store, new MultiplexHub());
        fans.Temperature = 55f;
        engine.Tick();

        // "Follow CPU" mirrors Fan #1 at 90% (proportional, -10), and Fan #1 is
        // driven by the graph curve in this same tick.
        var lead = fans.Driven.First(d => d.Id == "/lpc/nct6797d/0/control/0").Duty;
        var follower = fans.Driven.First(d => d.Id == "/lpc/nct6797d/0/control/3").Duty;
        Assert.Equal((int)Math.Round(lead * 0.9), follower);
    }

    [Fact]
    public void Apply_ImportsTheManualFanAndTheEngineReplaysIt()
    {
        var (import, fans, store) = Build();
        import.Apply(null, AllCategories);

        Assert.Equal(65, store.Load().Cooling.ManualSpeeds["/lpc/nct6797d/0/control/2"]);

        var engine = new CurveEngine(fans, store, new MultiplexHub());
        engine.Tick();
        Assert.Equal(65, fans.Duty["/lpc/nct6797d/0/control/2"]);
    }

    [Fact]
    public void Apply_AppliesTheImportedOffsetToTheDrivenDuty()
    {
        var (import, fans, store) = Build();
        import.Apply(null, AllCategories);

        var engine = new CurveEngine(fans, store, new MultiplexHub());
        fans.Temperature = 55f;
        engine.Tick();
        var withOffset = fans.Driven.First(d => d.Id == "/lpc/nct6797d/0/control/0").Duty;

        // Same tick without the offset, for the difference.
        var (import2, fans2, store2) = Build();
        import2.Apply(null, new[] { FanControlImportService.CategoryCurves });
        var engine2 = new CurveEngine(fans2, store2, new MultiplexHub());
        fans2.Temperature = 55f;
        engine2.Tick();
        var withoutOffset = fans2.Driven.First(d => d.Id == "/lpc/nct6797d/0/control/0").Duty;

        Assert.Equal(withoutOffset + 5, withOffset);
    }

    [Fact]
    public void ReApplying_ReplacesTheEarlierImportInsteadOfStacking()
    {
        var (import, _, store) = Build();
        import.Apply(null, AllCategories);
        var first = store.Load().Cooling.Curves.Count;

        import.Apply(null, AllCategories);
        Assert.Equal(first, store.Load().Cooling.Curves.Count);
    }

    [Fact]
    public void Apply_DoesNotTouchCurvesTheUserMadeHere()
    {
        var (import, _, store) = Build();
        store.Update(s => s.Cooling.Curves.Add(new CurveDocument
        {
            Id = "mine", Name = "Mine", Type = "Flat", Flat = new FlatCurveData { Speed = 33 },
        }));

        import.Apply(null, AllCategories);

        var mine = store.Load().Cooling.Curves.FirstOrDefault(c => c.Id == "mine");
        Assert.NotNull(mine);
        Assert.Equal(33, mine!.Flat!.Speed);
    }

    [Fact]
    public void Apply_ReleasesAChannelTheImportClaimsFromAUserCurve()
    {
        var (import, _, store) = Build();
        store.Update(s => s.Cooling.Curves.Add(new CurveDocument
        {
            Id = "mine", Name = "Mine", Type = "Flat", Flat = new FlatCurveData { Speed = 33 },
            Outputs = { new CurveOutputDocument { Id = "/lpc/nct6797d/0/control/0", Type = "Fan" } },
        }));

        import.Apply(null, AllCategories);

        // Two curves driving one channel would fight every tick.
        var owners = store.Load().Cooling.Curves
            .Where(c => c.Outputs.Any(o => o.Id == "/lpc/nct6797d/0/control/0"))
            .Select(c => c.Id)
            .ToList();
        Assert.Single(owners);
        Assert.StartsWith("fc-", owners[0]);
    }

    [Fact]
    public void Apply_WithNoCategories_ChangesNothing()
    {
        var (import, _, store) = Build();
        var result = import.Apply(null, Array.Empty<string>());
        Assert.True(result.Error);
        Assert.Empty(store.Load().Cooling.Curves);
    }
}
