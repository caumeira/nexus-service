using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Migration.FanControl;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Parsing and mapping of FanControl configurations.
///
/// Three fixtures. <c>userConfig-real.json</c> is a byte copy of a real
/// FanControl v215 config off the T1 lab box: it is what pins the identifier
/// handling, because it carries both quirks that matter (an LHM identifier
/// format one version behind ours, and NVIDIA fans addressed through
/// FanControl's own NvAPI plugin). <c>all-curve-kinds.json</c> is
/// hand-authored, since that box has only a flat curve; its key sets come from
/// the I*FanCurveConfig interfaces in FanControl.Library.dll rather than from
/// guesswork, but it is not a captured file. <c>userConfig-v275.json</c> is a
/// byte copy of a user's v275 config (an ASUS NCT6701D board with an RTX 5090):
/// v275 renamed the data section and reshaped the graph curves, so it is what
/// pins the newer layout.
/// </summary>
public class FanControlImportTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine("Fixtures", "FanControl", name));

    private static FanControlConfig Real() => FanControlConfigParser.Parse(Fixture("userConfig-real.json"));

    private static FanControlConfig AllKinds() => FanControlConfigParser.Parse(Fixture("all-curve-kinds.json"));

    private static FanControlConfig V275() => FanControlConfigParser.Parse(Fixture("userConfig-v275.json"));

    private static List<LhmIdentifierMatcher.Candidate> Channels(params (string Id, string Name)[] items) =>
        items.Select(i => new LhmIdentifierMatcher.Candidate(i.Id, i.Name)).ToList();

    // ── Parsing ───────────────────────────────────────────────────────────

    [Fact]
    public void Parse_RealConfig_ReadsVersionControlsAndCurves()
    {
        var config = Real();
        Assert.Equal(215, config.Version);
        Assert.Equal(8, config.Controls.Count);
        Assert.Single(config.Curves);
        Assert.Equal(FanControlCurveKinds.Flat, config.Curves[0].Kind);
        Assert.Equal(50, config.Curves[0].Percent);
    }

    [Fact]
    public void Parse_RealConfig_ReadsCalibrationTables()
    {
        var control = Real().Controls.First(c => c.Identifier == "/lpc/it8696e/control/0");
        Assert.Equal(11, control.Calibration.Count);
        Assert.Equal((0, 0), control.Calibration[0]);
        Assert.Equal((100, 2744), control.Calibration[^1]);
        Assert.Equal("CPU Fan", control.NickName);
    }

    [Fact]
    public void Parse_DetectsEveryCurveKind()
    {
        var kinds = AllKinds().Curves.ToDictionary(c => c.Name, c => c.Kind);
        Assert.Equal(FanControlCurveKinds.Graph, kinds["CPU Graph"]);
        Assert.Equal(FanControlCurveKinds.Linear, kinds["GPU Linear"]);
        Assert.Equal(FanControlCurveKinds.Trigger, kinds["Case Trigger"]);
        Assert.Equal(FanControlCurveKinds.Auto, kinds["CPU Auto"]);
        Assert.Equal(FanControlCurveKinds.Sync, kinds["Follow CPU"]);
        Assert.Equal(FanControlCurveKinds.Mix, kinds["Hottest Of Both"]);
        Assert.Equal(FanControlCurveKinds.Flat, kinds["Flat Half"]);
    }

    [Fact]
    public void Parse_GraphPointsComeFromCommaSeparatedStrings()
    {
        var graph = AllKinds().Curves.First(c => c.Name == "CPU Graph");
        Assert.Equal(5, graph.Points.Count);
        Assert.Equal(20, graph.Points[0].Temp);
        Assert.Equal(100, graph.Points[^1].Speed);
        Assert.Equal(80, graph.Points[^1].Temp);
    }

    [Fact]
    public void Parse_ReadsCurveBindingFromTheEmbeddedObject()
    {
        var control = AllKinds().Controls.First(c => c.Identifier == "/lpc/nct6797d/control/0");
        Assert.Equal("CPU Graph", control.SelectedFanCurveName);
    }

    // ── Identifier matching ───────────────────────────────────────────────

    [Fact]
    public void Normalize_DropsTheChipInstanceSegment()
    {
        Assert.Equal(
            LhmIdentifierMatcher.Normalize("/lpc/it8696e/0/control/0"),
            LhmIdentifierMatcher.Normalize("/lpc/it8696e/control/0"));
    }

    [Fact]
    public void Normalize_KeepsTheTrailingSensorIndex()
    {
        Assert.NotEqual(
            LhmIdentifierMatcher.Normalize("/lpc/it8696e/control/0"),
            LhmIdentifierMatcher.Normalize("/lpc/it8696e/control/1"));
    }

    [Fact]
    public void Match_PrefersAnExactIdentifier()
    {
        var candidates = Channels(("/lpc/it8696e/0/control/0", "CPU"), ("/lpc/it8696e/control/0", "Fan #1"));
        var (id, tier) = LhmIdentifierMatcher.Match("/lpc/it8696e/control/0", candidates, "Fan #1");
        Assert.Equal("/lpc/it8696e/control/0", id);
        Assert.Equal(IdentifierMatchTier.Exact, tier);
    }

    [Fact]
    public void Match_BridgesTheLhmVersionDifference()
    {
        // The exact case measured on T1: FanControl's identifier is one LHM
        // version behind ours for the same motherboard fan.
        var candidates = Channels(
            ("/lpc/it8696e/0/control/0", "CPU"),
            ("/lpc/it8696e/0/control/1", "Fan #2"));
        var (id, tier) = LhmIdentifierMatcher.Match("/lpc/it8696e/control/1", candidates, "Fan #2");
        Assert.Equal("/lpc/it8696e/0/control/1", id);
        Assert.Equal(IdentifierMatchTier.Normalized, tier);
    }

    [Fact]
    public void Match_AmbiguousNormalizedCandidates_StayUnmatched()
    {
        var candidates = Channels(("/lpc/x/0/control/0", "A"), ("/lpc/x/control/0", "B"));
        var (id, tier) = LhmIdentifierMatcher.Match("/lpc/x/0/0/control/0", candidates, null);
        Assert.Null(id);
        Assert.Equal(IdentifierMatchTier.None, tier);
    }

    [Fact]
    public void Match_FallsBackToAUniqueSensorName()
    {
        var candidates = Channels(("/some/other/control/0", "Fan #7"));
        var (id, tier) = LhmIdentifierMatcher.Match("/lpc/foo/control/0", candidates, "Fan #7");
        Assert.Equal("/some/other/control/0", id);
        Assert.Equal(IdentifierMatchTier.Name, tier);
    }

    [Fact]
    public void Match_DuplicateNames_StayUnmatched()
    {
        var candidates = Channels(("/a/control/0", "Fan #1"), ("/b/control/0", "Fan #1"));
        var (id, _) = LhmIdentifierMatcher.Match("/lpc/foo/control/0", candidates, "Fan #1");
        Assert.Null(id);
    }

    [Fact]
    public void PairGpuByPosition_BridgesTheNvApiNamespace()
    {
        var pairs = LhmIdentifierMatcher.PairGpuByPosition(
            new[] { "NVApiWrapper/0-GA102-A/control/0", "NVApiWrapper/0-GA102-A/control/1" },
            Channels(("/gpu-nvidia/0/control/1", "GPU Fan 1"), ("/gpu-nvidia/0/control/2", "GPU Fan 2")));
        Assert.Equal("/gpu-nvidia/0/control/1", pairs["NVApiWrapper/0-GA102-A/control/0"]);
        Assert.Equal("/gpu-nvidia/0/control/2", pairs["NVApiWrapper/0-GA102-A/control/1"]);
    }

    [Fact]
    public void PairGpuByPosition_MismatchedCounts_PairNothing()
    {
        var pairs = LhmIdentifierMatcher.PairGpuByPosition(
            new[] { "NVApiWrapper/0-GA102-A/control/0", "NVApiWrapper/0-GA102-A/control/1" },
            Channels(("/gpu-nvidia/0/control/1", "GPU Fan 1")));
        Assert.Empty(pairs);
    }

    [Fact]
    public void PairGpuByPosition_IgnoresNonGpuLeftovers()
    {
        var pairs = LhmIdentifierMatcher.PairGpuByPosition(
            new[] { "/lpc/it8696e/control/7" },
            Channels(("/lpc/it8696e/0/control/3", "Fan #4")));
        Assert.Empty(pairs);
    }

    // ── Mapping ───────────────────────────────────────────────────────────

    private static readonly List<LhmIdentifierMatcher.Candidate> AllKindsChannels = Channels(
        ("/lpc/nct6797d/0/control/0", "Fan #1"),
        ("/lpc/nct6797d/0/control/1", "Fan #2"),
        ("/lpc/nct6797d/0/control/2", "Fan #3"),
        ("/lpc/nct6797d/0/control/3", "Fan #4"));

    private static readonly List<LhmIdentifierMatcher.Candidate> AllKindsSensors = Channels(
        ("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)"),
        ("/gpu-nvidia/0/temperature/0", "GPU Core"),
        ("/lpc/nct6797d/0/temperature/1", "Temperature #2"));

    private static FanControlImportPlan Plan() =>
        FanControlImportMapper.Build(AllKinds(), AllKindsChannels, AllKindsSensors);

    [Fact]
    public void Map_TranslatesEverySupportedKind()
    {
        var byName = Plan().Curves.ToDictionary(c => c.Name, c => c.Type);
        Assert.Equal("Graph", byName["CPU Graph"]);
        Assert.Equal("Linear", byName["GPU Linear"]);
        Assert.Equal("Trigger", byName["Case Trigger"]);
        Assert.Equal("Auto", byName["CPU Auto"]);
        Assert.Equal("Sync", byName["Follow CPU"]);
        Assert.Equal("Mixed", byName["Hottest Of Both"]);
        Assert.Equal("Flat", byName["Flat Half"]);
    }

    [Fact]
    public void Map_GraphKeepsItsPointsAndSensor()
    {
        var graph = Plan().Curves.First(c => c.Name == "CPU Graph");
        Assert.Equal("/amdcpu/0/temperature/2", graph.Input.Id);
        Assert.NotNull(graph.Graph);
        Assert.Equal(5, graph.Graph!.Points.Count);
        Assert.Equal(5, graph.Graph.ResponseTime);
    }

    [Fact]
    public void Map_TriggerKeepsBothThresholds()
    {
        var trigger = Plan().Curves.First(c => c.Name == "Case Trigger").Trigger;
        Assert.NotNull(trigger);
        Assert.Equal(45, trigger!.IdleTemp);
        Assert.Equal(65, trigger.LoadTemp);
        Assert.Equal(25, trigger.IdleSpeed);
        Assert.Equal(80, trigger.LoadSpeed);
        Assert.Equal(3, trigger.ResponseTime);
    }

    [Fact]
    public void Map_AutoKeepsStepAndDeadband()
    {
        var auto = Plan().Curves.First(c => c.Name == "CPU Auto").Auto;
        Assert.NotNull(auto);
        Assert.Equal(40, auto!.IdleTemp);
        Assert.Equal(70, auto.LoadTemp);
        Assert.Equal(25, auto.MinSpeed);
        Assert.Equal(95, auto.MaxSpeed);
        Assert.Equal(4, auto.Step);
        Assert.Equal(3, auto.Deadband);
    }

    [Fact]
    public void Map_SyncBindsToTheMatchedChannel()
    {
        var sync = Plan().Curves.First(c => c.Name == "Follow CPU").Sync;
        Assert.NotNull(sync);
        Assert.Equal("/lpc/nct6797d/0/control/0", sync!.SourceChannelId);
        Assert.True(sync.Proportional);
        Assert.Equal(-10, sync.Offset);
    }

    [Fact]
    public void Map_MixResolvesMembersToCurveIds()
    {
        var plan = Plan();
        var mix = plan.Curves.First(c => c.Name == "Hottest Of Both");
        var graphId = plan.Curves.First(c => c.Name == "CPU Graph").Id;
        var linearId = plan.Curves.First(c => c.Name == "GPU Linear").Id;
        Assert.NotNull(mix.Mixed);
        Assert.Equal("max", mix.Mixed!.Fn);
        Assert.Equal(new[] { graphId, linearId }, mix.Mixed.CurveIds);
    }

    [Fact]
    public void Map_BindsCurvesToTheFansThatSelectedThem()
    {
        var graph = Plan().Curves.First(c => c.Name == "CPU Graph");
        Assert.Equal(new[] { "/lpc/nct6797d/0/control/0" }, graph.Outputs.Select(o => o.Id));
    }

    [Fact]
    public void Map_SkipsRpmTargetCurves()
    {
        var plan = Plan();
        Assert.DoesNotContain(plan.Curves, c => c.Name == "RPM Target");
        var preview = plan.Preview.Curves.First(c => c.Name == "RPM Target");
        Assert.False(preview.Supported);
        Assert.Equal("rpmMode", preview.ReasonCode);
    }

    [Fact]
    public void Map_SkipsHiddenCurvesNothingReferences()
    {
        var plan = Plan();
        Assert.DoesNotContain(plan.Curves, c => c.Name == "Hidden Spare");
        Assert.DoesNotContain(plan.Preview.Curves, c => c.Name == "Hidden Spare");
    }

    [Fact]
    public void Map_ImportsNicknamesOnlyWhenTheyDifferFromTheHardwareName()
    {
        var names = Plan().Names;
        Assert.Equal("CPU Fan", names["/lpc/nct6797d/0/control/0"]);
        Assert.Equal("Front Intake", names["/lpc/nct6797d/0/control/1"]);
        // Fan #3's nickname is just its hardware name.
        Assert.False(names.ContainsKey("/lpc/nct6797d/0/control/2"));
    }

    [Fact]
    public void Map_ImportsCalibrationAndDerivesTheDutyFloor()
    {
        var calibration = Plan().Calibrations["/lpc/nct6797d/0/control/0"];
        Assert.Equal(11, calibration.Curve.Count);
        Assert.Equal(2100, calibration.MaxRpm);
        // MinimumPercent (20) raises the floor over the lowest spinning step (10).
        Assert.Equal(20, calibration.MinDuty);
    }

    [Fact]
    public void Map_ImportsManualDutyForAFanOnManualControl()
    {
        Assert.Equal(65, Plan().ManualSpeeds["/lpc/nct6797d/0/control/2"]);
    }

    [Fact]
    public void Map_ImportsPerFanOffsets()
    {
        var offsets = Plan().Offsets;
        Assert.Equal(5, offsets["/lpc/nct6797d/0/control/0"]);
        Assert.Single(offsets);
    }

    [Fact]
    public void Map_ReportsFansThisPcDoesNotHave()
    {
        var plan = Plan();
        var missing = plan.Preview.Fans.First(f => f.Identifier == "/lpc/nct6797d/control/9");
        Assert.Null(missing.ChannelId);
        Assert.Equal("none", missing.Match);
    }

    [Fact]
    public void Map_ReportsHowEachFanWasMatched()
    {
        var fan = Plan().Preview.Fans.First(f => f.Identifier == "/lpc/nct6797d/control/0");
        Assert.Equal("normalized", fan.Match);
        Assert.Equal("/lpc/nct6797d/0/control/0", fan.ChannelId);
    }

    [Fact]
    public void Map_MatchesASensorByItsNameWhenTheIdentifierDiffers()
    {
        // Both apps read these from LibreHardwareMonitor, so the name is the
        // same string on both sides even when the identifier is not.
        var plan = FanControlImportMapper.Build(
            AllKinds(),
            AllKindsChannels,
            Channels(
                ("/some/other/path/temperature/9", "Core (Tctl/Tdie)"),
                ("/gpu-nvidia/0/temperature/0", "GPU Core"),
                ("/lpc/nct6797d/0/temperature/1", "Temperature #2")));

        var graph = plan.Curves.First(c => c.Name == "CPU Graph");
        Assert.Equal("/some/other/path/temperature/9", graph.Input.Id);
    }

    [Fact]
    public void Map_PairsAGpuSensorByPositionWhenNothingElseBridgesIt()
    {
        // FanControl reads NVIDIA temperatures through its own NvAPI plugin,
        // whose identifiers share nothing with LHM's.
        var config = FanControlConfigParser.Parse(Fixture("all-curve-kinds.json").Replace(
            "/gpu-nvidia/0/temperature/0", "NVApiWrapper/0-GA102-A/temperature/0"));
        var plan = FanControlImportMapper.Build(
            config,
            AllKindsChannels,
            Channels(
                ("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)"),
                ("/gpu-nvidia/0/temperature/0", "GPU Core"),
                ("/lpc/nct6797d/0/temperature/1", "Temperature #2")));

        var linear = plan.Curves.First(c => c.Name == "GPU Linear");
        Assert.Equal("/gpu-nvidia/0/temperature/0", linear.Input.Id);
    }

    [Fact]
    public void Map_CurvesOnOneSourceShareIt_AndTheRestAreReported()
    {
        // Two FanControl curves watching one sensor must both watch ours; two
        // DIFFERENT sources must not collapse onto the same local sensor, or a
        // curve quietly drives off the wrong reading.
        var plan = FanControlImportMapper.Build(
            AllKinds(),
            AllKindsChannels,
            Channels(("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)")));

        // CPU Graph and CPU Auto both point at the CPU sensor in the fixture.
        var cpuBound = plan.Curves
            .Where(c => c.Name is "CPU Graph" or "CPU Auto")
            .Select(c => c.Input.Id)
            .ToList();
        Assert.Equal(new[] { "/amdcpu/0/temperature/2", "/amdcpu/0/temperature/2" }, cpuBound);

        // The GPU and motherboard sources have nothing to match here.
        foreach (var name in new[] { "GPU Linear", "Case Trigger" })
        {
            var preview = plan.Preview.Curves.First(c => c.Name == name);
            Assert.False(preview.Supported);
            Assert.Equal("noSensor", preview.ReasonCode);
        }
    }

    [Fact]
    public void Map_CurveNeedingAnAbsentSensorIsReportedNotDropped()
    {
        var plan = FanControlImportMapper.Build(
            AllKinds(),
            AllKindsChannels,
            Channels(("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)")));
        Assert.DoesNotContain(plan.Curves, c => c.Name == "GPU Linear");
        var preview = plan.Preview.Curves.First(c => c.Name == "GPU Linear");
        Assert.False(preview.Supported);
        Assert.Equal("noSensor", preview.ReasonCode);
    }

    [Fact]
    public void Map_RealConfig_MatchesEveryMotherboardFanAndPairsTheGpuFans()
    {
        var channels = Channels(
            ("/lpc/it8696e/0/control/0", "CPU"),
            ("/lpc/it8696e/0/control/1", "Fan #2"),
            ("/lpc/it8696e/0/control/2", "Fan #3"),
            ("/lpc/it8696e/0/control/4", "Fan #5"),
            ("/gpu-nvidia/0/control/1", "GPU Fan 1"),
            ("/gpu-nvidia/0/control/2", "GPU Fan 2"));
        var plan = FanControlImportMapper.Build(Real(), channels, AllKindsSensors);

        var byIdentifier = plan.Preview.Fans.ToDictionary(f => f.Identifier);
        Assert.Equal("/lpc/it8696e/0/control/0", byIdentifier["/lpc/it8696e/control/0"].ChannelId);
        Assert.Equal("normalized", byIdentifier["/lpc/it8696e/control/0"].Match);
        Assert.Equal("/gpu-nvidia/0/control/1", byIdentifier["NVApiWrapper/0-GA102-A/control/0"].ChannelId);
        Assert.Equal("position", byIdentifier["NVApiWrapper/0-GA102-A/control/0"].Match);
        Assert.Equal("/gpu-nvidia/0/control/2", byIdentifier["NVApiWrapper/0-GA102-A/control/1"].ChannelId);

        // Fans 4 and 6 exist in FanControl but not in our enumeration.
        Assert.Null(byIdentifier["/lpc/it8696e/control/3"].ChannelId);
        Assert.Null(byIdentifier["/lpc/it8696e/control/5"].ChannelId);
    }

    [Fact]
    public void Map_RealConfig_ImportsTheCalibrationsFanControlAlreadyMeasured()
    {
        var channels = Channels(
            ("/lpc/it8696e/0/control/0", "CPU"),
            ("/lpc/it8696e/0/control/1", "Fan #2"),
            ("/lpc/it8696e/0/control/4", "Fan #5"),
            ("/gpu-nvidia/0/control/1", "GPU Fan 1"),
            ("/gpu-nvidia/0/control/2", "GPU Fan 2"));
        var plan = FanControlImportMapper.Build(Real(), channels, AllKindsSensors);

        Assert.Equal(5, plan.Calibrations.Count);
        Assert.Equal(2744, plan.Calibrations["/lpc/it8696e/0/control/0"].MaxRpm);
        Assert.Equal("CPU Fan", plan.Names["/lpc/it8696e/0/control/0"]);
    }

    // ── Config v275 layout ────────────────────────────────────────────────

    [Fact]
    public void Parse_V275_ReadsTheRenamedDataSection()
    {
        var config = V275();
        Assert.Equal(275, config.Version);
        Assert.Equal(9, config.Controls.Count);
        Assert.Equal(5, config.Curves.Count);
    }

    [Fact]
    public void Parse_V275_DetectsCurveKinds()
    {
        var kinds = V275().Curves.ToDictionary(c => c.Name, c => c.Kind);
        Assert.Equal(FanControlCurveKinds.Flat, kinds["Flat"]);
        Assert.Equal(FanControlCurveKinds.Graph, kinds["CPU Fan Curve"]);
        Assert.Equal(FanControlCurveKinds.Graph, kinds["GPU Fan Curve"]);
        Assert.Equal(FanControlCurveKinds.Auto, kinds["Auto CPU"]);
        Assert.Equal(FanControlCurveKinds.Auto, kinds["Auto GPU"]);
    }

    [Fact]
    public void Parse_V275_ReadsGraphResponseTimeFromHysteresisConfig()
    {
        var graph = FanControlConfigParser.Parse(
            Fixture("userConfig-v275.json").Replace("\"ResponseTimeUp\": 1,", "\"ResponseTimeUp\": 4,"))
            .Curves.First(c => c.Name == "CPU Fan Curve");
        Assert.Equal(4, graph.ResponseTime);
    }

    [Fact]
    public void Parse_V275_ReadsCalibrationRowsThatCarryATrailingFlag()
    {
        // v275 calibration rows are [percent, rpm, bool] where v215's were pairs.
        var control = V275().Controls.First(c => c.Identifier == "/lpc/nct6701d/control/1");
        Assert.Equal(11, control.Calibration.Count);
        Assert.Equal((1, 0), control.Calibration[0]);
        Assert.Equal((100, 1737), control.Calibration[^1]);
        Assert.Equal("AIO Fans", control.NickName);
    }

    [Fact]
    public void Map_V275_ImportsOnlyTheNamesTheUserActuallyChanged()
    {
        // v275 drops Control.Name, so a nickname is a rename only when it differs
        // from the paired fan sensor's label. Without that comparison every
        // FanControl default ("AIO Pump", "Control 1 - NVIDIA GeForce RTX 5090")
        // would overwrite the user's own fan names.
        var channels = Channels(
            ("/lpc/nct6701d/0/control/0", "Fan #1"),
            ("/lpc/nct6701d/0/control/1", "Fan #2"),
            ("/lpc/nct6701d/0/control/2", "Fan #3"),
            ("/lpc/nct6701d/0/control/4", "Fan #5"),
            ("/lpc/nct6701d/0/control/6", "Fan #7"),
            ("/gpu-nvidia/0/control/0", "GPU Fan 1"),
            ("/gpu-nvidia/0/control/1", "GPU Fan 2"));
        var plan = FanControlImportMapper.Build(V275(), channels, AllKindsSensors);
        var names = plan.Names;

        Assert.Equal("Front & Rear Fans", names["/lpc/nct6701d/0/control/0"]);
        Assert.Equal("AIO Fans", names["/lpc/nct6701d/0/control/1"]);
        Assert.Equal("Bottom Fans", names["/lpc/nct6701d/0/control/4"]);
        Assert.Equal(3, names.Count);

        // Every excluded control below bound to a channel, so exclusion is the
        // predicate's doing and not a failed match.
        foreach (var id in new[]
                 {
                     "/lpc/nct6701d/0/control/2", "/lpc/nct6701d/0/control/6",
                     "/gpu-nvidia/0/control/0", "/gpu-nvidia/0/control/1",
                 })
        {
            Assert.Contains(plan.Preview.Fans, f => f.ChannelId == id);
            Assert.DoesNotContain(id, names.Keys);
        }
    }

    [Fact]
    public void Map_V275_DropsANicknameItCannotTellApartFromTheGeneratedLabel()
    {
        // The two branches that exclude a name are different and only one is the
        // equality test: the GPU controls are paired and their nickname equals the
        // paired sensor's label, while control/2 and control/6 have no paired
        // sensor at all, so nothing is left to compare against.
        var config = V275();
        var gpu = config.Controls.First(c => c.Identifier == "NVApiWrapper/0-GB202-A/control/0");
        Assert.Equal(
            config.FanSensorNames[gpu.PairedFanSensorIdentifier!],
            gpu.NickName);

        foreach (var id in new[] { "/lpc/nct6701d/control/2", "/lpc/nct6701d/control/6" })
        {
            Assert.Null(config.Controls.First(c => c.Identifier == id).PairedFanSensorIdentifier);
        }
    }

    [Fact]
    public void Parse_ReadsTheGeneratedFanLabel_FromNameOnV215_AndNickNameOnV275()
    {
        // v215 carries both, and there NickName mirrors the paired control's label
        // rather than the hardware - so Name is the one that means "generated".
        Assert.Equal("Fan #1", Real().FanSensorNames["/lpc/it8696e/fan/0"]);
        Assert.Equal(
            "Fan 1 - NVIDIA GeForce RTX 3080",
            Real().FanSensorNames["NVApiWrapper/0-GA102-A/fan/0"]);

        var config = V275();
        Assert.Equal("Chassis Fan #1", config.FanSensorNames["/lpc/nct6701d/fan/0"]);
        Assert.Equal(
            "/lpc/nct6701d/fan/1",
            config.Controls.First(c => c.Identifier == "/lpc/nct6701d/control/1").PairedFanSensorIdentifier);
        Assert.Null(config.Controls.First(c => c.Identifier == "/lpc/nct6701d/control/2").PairedFanSensorIdentifier);
    }

    [Fact]
    public void Map_V275_BindsTheReportedConfigToThisPcsFans()
    {
        var channels = Channels(
            ("/lpc/nct6701d/0/control/0", "Fan #1"),
            ("/lpc/nct6701d/0/control/1", "Fan #2"),
            ("/lpc/nct6701d/0/control/4", "Fan #5"),
            ("/gpu-nvidia/0/control/0", "GPU Fan 1"),
            ("/gpu-nvidia/0/control/1", "GPU Fan 2"));
        var sensors = Channels(
            ("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)"),
            ("/amdcpu/0/temperature/3", "CCD1 (Tdie)"),
            ("/gpu-nvidia/0/temperature/0", "GPU Core"));
        var plan = FanControlImportMapper.Build(V275(), channels, sensors);

        var byIdentifier = plan.Preview.Fans.ToDictionary(f => f.Identifier);
        Assert.Equal("/lpc/nct6701d/0/control/1", byIdentifier["/lpc/nct6701d/control/1"].ChannelId);
        Assert.Equal("normalized", byIdentifier["/lpc/nct6701d/control/1"].Match);
        Assert.Equal("/gpu-nvidia/0/control/0", byIdentifier["NVApiWrapper/0-GB202-A/control/0"].ChannelId);
        Assert.Equal("position", byIdentifier["NVApiWrapper/0-GB202-A/control/0"].Match);

        var gpuCurve = plan.Curves.First(c => c.Name == "GPU Fan Curve");
        Assert.Equal("/gpu-nvidia/0/temperature/0", gpuCurve.Input.Id);
        Assert.Equal(
            new[] { "/lpc/nct6701d/0/control/0", "/lpc/nct6701d/0/control/4" },
            gpuCurve.Outputs.Select(o => o.Id).Order());
        Assert.Equal(
            new[] { "/lpc/nct6701d/0/control/1" },
            plan.Curves.First(c => c.Name == "Auto CPU").Outputs.Select(o => o.Id));
        Assert.Equal("AIO Fans", plan.Names["/lpc/nct6701d/0/control/1"]);
    }

    [Fact]
    public void UniqueId_DeduplicatesRepeatedNames()
    {
        var used = new HashSet<string>();
        Assert.Equal("fc-cpu-graph", FanControlImportMapper.UniqueId("CPU Graph", used));
        Assert.Equal("fc-cpu-graph-2", FanControlImportMapper.UniqueId("CPU Graph", used));
    }

    [Fact]
    public void UniqueId_FallsBackForANamelessCurve()
    {
        Assert.Equal("fc-curve", FanControlImportMapper.UniqueId("", new HashSet<string>()));
    }
}
