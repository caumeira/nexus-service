using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;

namespace Nexus.Service.Migration.FanControl;

/// <summary>Everything an import would write, plus the preview describing it.</summary>
internal sealed class FanControlImportPlan
{
    public List<CurveDocument> Curves { get; } = new();
    public Dictionary<string, FanCalibration> Calibrations { get; } = new();
    public Dictionary<string, string> Names { get; } = new();
    public Dictionary<string, int> Offsets { get; } = new();
    public Dictionary<string, int> ManualSpeeds { get; } = new();
    public FanControlPreviewResponse Preview { get; } = new();
}

/// <summary>
/// Maps a parsed FanControl config onto Nexus curves, calibrations and per-fan
/// settings. Pure: it takes the local channels and sensors as candidates, so
/// the whole mapping is testable without hardware.
/// </summary>
internal static class FanControlImportMapper
{
    /// <summary>FanControl's MixFunction enum order, as serialized.</summary>
    private static readonly string[] MixFunctions = { "max", "sum", "avg", "min", "subtract" };

    public static FanControlImportPlan Build(
        FanControlConfig config,
        IReadOnlyList<LhmIdentifierMatcher.Candidate> channels,
        IReadOnlyList<LhmIdentifierMatcher.Candidate> sensors)
    {
        var plan = new FanControlImportPlan();
        plan.Preview.Available = true;
        plan.Preview.Version = config.Version;

        // Controls first: curves bind to the channels they resolve to.
        var controlMatches = new Dictionary<string, ControlMatch>(StringComparer.OrdinalIgnoreCase);
        var resolved = new Dictionary<string, (string? Id, IdentifierMatchTier Tier)>(StringComparer.OrdinalIgnoreCase);
        var takenChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in config.Controls)
        {
            // Only fans nothing has claimed yet: two controls resolving to one
            // channel would put two curves on one fan.
            var free = channels.Where(c => !takenChannels.Contains(c.Id)).ToList();
            var match = LhmIdentifierMatcher.Match(control.Identifier, free, control.Name);
            resolved[control.Identifier] = match;
            if (match.Id is not null)
            {
                takenChannels.Add(match.Id);
            }
        }

        // GPU fans never match by identifier (FanControl uses its own NvAPI /
        // ADLX plugins) and rarely by name, so pair the leftovers by position.
        var gpuPairs = LhmIdentifierMatcher.PairGpuByPosition(
            resolved.Where(r => r.Value.Id is null).Select(r => r.Key).ToList(),
            channels.Where(c => !takenChannels.Contains(c.Id)).ToList());
        foreach (var (source, target) in gpuPairs)
        {
            resolved[source] = (target, IdentifierMatchTier.Ordinal);
            takenChannels.Add(target);
        }

        foreach (var control in config.Controls)
        {
            var (channelId, tier) = resolved[control.Identifier];
            var channelName = channelId is null
                ? null
                : channels.FirstOrDefault(c => c.Id == channelId).Name;
            var match = new ControlMatch(control, channelId, tier, channelName);
            controlMatches[control.Identifier] = match;

            plan.Preview.Fans.Add(new FanControlFanPreviewDto
            {
                Identifier = control.Identifier,
                SourceName = string.IsNullOrEmpty(control.NickName) ? control.Name : control.NickName!,
                ChannelId = channelId,
                ChannelName = channelName,
                Match = TierName(tier),
                NickName = Nickname(control),
                HasCalibration = control.Calibration.Count >= 2,
                ManualDuty = control.ManualControl && control.Enable
                    ? (int)Math.Round(control.ManualControlValue)
                    : null,
                Offset = (int)Math.Round(control.SelectedOffset),
                CurveName = control.Enable ? control.SelectedFanCurveName : null,
            });

            if (channelId is null)
            {
                continue;
            }

            if (Nickname(control) is { } nickname)
            {
                plan.Names[channelId] = nickname;
            }

            if (control.Calibration.Count >= 2)
            {
                plan.Calibrations[channelId] = BuildCalibration(channelId, control);
            }

            var offset = (int)Math.Round(control.SelectedOffset);
            if (offset != 0)
            {
                plan.Offsets[channelId] = Math.Clamp(offset, -100, 100);
            }

            if (control.Enable && control.ManualControl)
            {
                plan.ManualSpeeds[channelId] = CoolingSafety.ClampDuty((int)Math.Round(control.ManualControlValue));
            }
        }

        // Temperature sources, resolved once for the whole config: both apps read
        // these from LibreHardwareMonitor, so the sensor's own name bridges the
        // cases the identifier cannot, and GPU sensors need the same positional
        // pairing the GPU fans do.
        var sensorMatches = ResolveSensors(config, sensors);

        // Which controls each curve drives, by curve name.
        var boundControls = new Dictionary<string, List<ControlMatch>>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in controlMatches.Values)
        {
            var curveName = match.Control.SelectedFanCurveName;
            if (!match.Control.Enable || match.Control.ManualControl || string.IsNullOrEmpty(curveName))
            {
                continue;
            }
            if (!boundControls.TryGetValue(curveName, out var list))
            {
                list = new List<ControlMatch>();
                boundControls[curveName] = list;
            }
            list.Add(match);
        }

        var referenced = ReferencedCurveNames(config, boundControls);
        var idsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<(FanControlCurve Curve, CurveDocument Doc, FanControlCurvePreviewDto Preview)>();

        foreach (var curve in config.Curves)
        {
            // A hidden curve nothing points at is FanControl UI clutter.
            if (curve.IsHidden && !referenced.Contains(curve.Name))
            {
                continue;
            }

            var preview = new FanControlCurvePreviewDto
            {
                Name = curve.Name,
                SourceKind = curve.Kind,
                Supported = true,
            };
            plan.Preview.Curves.Add(preview);

            if (curve.CommandMode == 1)
            {
                Unsupported(preview, "rpmMode");
                continue;
            }

            var type = TargetType(curve.Kind);
            if (type is null)
            {
                Unsupported(preview, "unknownKind", curve.Kind);
                continue;
            }
            preview.TargetType = type;

            var doc = new CurveDocument
            {
                Id = UniqueId(curve.Name, usedIds),
                Name = string.IsNullOrWhiteSpace(curve.Name) ? "Imported curve" : curve.Name,
                Type = type,
            };

            if (CurveEngine.NeedsTemperature(type))
            {
                var sensorId = sensorMatches.GetValueOrDefault(curve.TempSourceIdentifier ?? "");
                if (sensorId is null)
                {
                    Unsupported(preview, "noSensor");
                    continue;
                }
                var sensor = sensors.FirstOrDefault(s => s.Id == sensorId);
                preview.SensorName = sensor.Name;
                doc.Input = new CurveInputDocument { Id = sensorId, Type = "Temperature" };
            }

            if (!Fill(curve, doc, controlMatches, preview))
            {
                continue;
            }

            if (boundControls.TryGetValue(curve.Name, out var bound))
            {
                foreach (var match in bound)
                {
                    if (match.ChannelId is null)
                    {
                        continue;
                    }
                    doc.Outputs.Add(new CurveOutputDocument { Id = match.ChannelId, Type = "Fan" });
                    preview.FanNames.Add(match.ChannelName ?? match.Control.Name);
                }
            }

            idsByName[curve.Name] = doc.Id;
            pending.Add((curve, doc, preview));
        }

        // Second pass: mix members reference other curves by name, which only
        // now have ids.
        foreach (var (curve, doc, preview) in pending)
        {
            if (doc.Type == "Mixed" && doc.Mixed is not null)
            {
                foreach (var memberName in curve.MixCurveNames)
                {
                    if (idsByName.TryGetValue(memberName, out var memberId))
                    {
                        doc.Mixed.CurveIds.Add(memberId);
                    }
                }
                if (doc.Mixed.CurveIds.Count == 0)
                {
                    Unsupported(preview, "noMixMembers");
                    continue;
                }
            }
            plan.Curves.Add(doc);
        }

        // A cycle here would come from FanControl's own graph, which forbids
        // them, but a hand-edited config could still carry one.
        foreach (var id in CurveOrdering.FindCycleMembers(plan.Curves))
        {
            var doc = plan.Curves.FirstOrDefault(c => c.Id == id);
            if (doc is null)
            {
                continue;
            }
            plan.Curves.Remove(doc);
            var preview = plan.Preview.Curves.FirstOrDefault(p => p.Name == doc.Name);
            if (preview is not null)
            {
                Unsupported(preview, "cycle");
            }
        }

        plan.Preview.CurveCount = plan.Curves.Count;
        plan.Preview.CalibrationCount = plan.Calibrations.Count;
        plan.Preview.NameCount = plan.Names.Count;
        plan.Preview.OffsetCount = plan.Offsets.Count;
        plan.Preview.ManualCount = plan.ManualSpeeds.Count;
        return plan;
    }

    /// <summary>
    /// Every temperature source the config's curves point at, resolved to a
    /// local sensor id (null when nothing here matches). Identifier, then the
    /// sensor's hardware-reported name - which is the same string on both sides,
    /// since both read LibreHardwareMonitor - then position within the GPU class,
    /// which is the only bridge for a GPU sensor FanControl reads through its own
    /// NvAPI or ADLX plugin.
    /// </summary>
    private static Dictionary<string, string?> ResolveSensors(
        FanControlConfig config, IReadOnlyList<LhmIdentifierMatcher.Candidate> sensors)
    {
        var wanted = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var curve in config.Curves)
        {
            var id = curve.TempSourceIdentifier;
            if (string.IsNullOrEmpty(id) || wanted.ContainsKey(id))
            {
                continue;
            }
            wanted[id] = null;
            names[id] = curve.TempSourceName;
        }

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in wanted.Keys.ToList())
        {
            var free = sensors.Where(c => !taken.Contains(c.Id)).ToList();
            var (match, _) = LhmIdentifierMatcher.Match(id, free, names[id]);
            wanted[id] = match;
            if (match is not null)
            {
                taken.Add(match);
            }
        }

        var gpuPairs = LhmIdentifierMatcher.PairGpuByPosition(
            wanted.Where(w => w.Value is null).Select(w => w.Key).ToList(),
            sensors.Where(c => !taken.Contains(c.Id)).ToList());
        foreach (var (source, target) in gpuPairs)
        {
            wanted[source] = target;
        }

        return wanted;
    }

    private readonly record struct ControlMatch(
        FanControlControl Control, string? ChannelId, IdentifierMatchTier Tier, string? ChannelName);

    /// <summary>Fills the type-specific data. False when the curve cannot be represented.</summary>
    private static bool Fill(
        FanControlCurve curve,
        CurveDocument doc,
        Dictionary<string, ControlMatch> controls,
        FanControlCurvePreviewDto preview)
    {
        switch (doc.Type)
        {
            case "Flat":
                doc.Flat = new FlatCurveData { Speed = (int)Math.Round(curve.Percent) };
                return true;

            case "Graph":
                if (curve.Points.Count == 0)
                {
                    Unsupported(preview, "noPoints");
                    return false;
                }
                if (curve.UnreadablePoints > 0)
                {
                    // Importing the readable half would be a different curve
                    // from the one the user drew.
                    Unsupported(preview, "badPoints");
                    return false;
                }
                doc.Graph = new GraphCurveData
                {
                    ResponseTime = curve.ResponseTime,
                    SpeedModifier = 1.0,
                    Points = curve.Points
                        .Select(p => new Nexus.Service.Persistence.GraphPoint { Temp = p.Temp, Speed = p.Speed })
                        .ToList(),
                };
                return true;

            case "Linear":
                doc.Linear = new LinearCurveData
                {
                    ResponseTime = curve.ResponseTime,
                    MinTemp = curve.MinimumTemperature,
                    MaxTemp = curve.MaximumTemperature,
                    MinSpeed = curve.MinimumFanSpeed,
                    MaxSpeed = curve.MaximumFanSpeed,
                };
                return true;

            case "Mixed":
                doc.Mixed = new MixedCurveData
                {
                    ResponseTime = curve.ResponseTime,
                    Fn = curve.MixFunction >= 0 && curve.MixFunction < MixFunctions.Length
                        ? MixFunctions[curve.MixFunction]
                        : "max",
                };
                return true;

            case "Trigger":
                if (curve.IdleFanSpeed <= 0 && curve.LoadFanSpeed <= 0)
                {
                    Unsupported(preview, "noSpeedRange");
                    return false;
                }
                doc.Trigger = new TriggerCurveData
                {
                    ResponseTime = curve.ResponseTime,
                    IdleTemp = curve.IdleTemperature,
                    LoadTemp = curve.LoadTemperature,
                    IdleSpeed = curve.IdleFanSpeed,
                    LoadSpeed = curve.LoadFanSpeed,
                };
                return true;

            case "Auto":
                if (curve.MaximumFanSpeed <= 0)
                {
                    Unsupported(preview, "noSpeedRange");
                    return false;
                }
                doc.Auto = new AutoCurveData
                {
                    ResponseTime = curve.ResponseTime,
                    IdleTemp = curve.IdleTemperature,
                    LoadTemp = curve.LoadTemperature,
                    MinSpeed = curve.MinimumFanSpeed,
                    MaxSpeed = curve.MaximumFanSpeed,
                    Step = curve.Step,
                    Deadband = curve.Deadband,
                };
                return true;

            case "Sync":
                var source = curve.SyncControlIdentifier ?? "";
                if (!controls.TryGetValue(source, out var match) || match.ChannelId is null)
                {
                    Unsupported(preview, "noSyncSource");
                    return false;
                }
                doc.Sync = new SyncCurveData
                {
                    SourceChannelId = match.ChannelId,
                    Offset = curve.SyncOffset,
                    Proportional = curve.SyncProportional,
                };
                preview.SensorName = match.ChannelName;
                return true;

            default:
                Unsupported(preview, "unknownKind", curve.Kind);
                return false;
        }
    }

    /// <summary>
    /// Reuses our own calibration classifier so an imported table produces the
    /// same MinRpm / MaxRpm / MinDuty / classification a Nexus calibration run
    /// would. FanControl sweeps upward, so its lowest spinning duty is the
    /// start duty rather than the sustain floor: a slightly conservative floor.
    /// </summary>
    private static FanCalibration BuildCalibration(string channelId, FanControlControl control)
    {
        var points = control.Calibration
            .OrderBy(p => p.Percent)
            .Select(p => new FanCalibrationPoint
            {
                Duty = CoolingSafety.ClampDuty(p.Percent),
                Rpm = Math.Max(0, p.Rpm),
            })
            .ToList();

        var calibration = FanCalibrationLogic.Classify(channelId, points);
        if (control.MinimumPercent > 0)
        {
            calibration.MinDuty = Math.Max(calibration.MinDuty, CoolingSafety.ClampDuty(control.MinimumPercent));
        }
        calibration.CalibratedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return calibration;
    }

    private static HashSet<string> ReferencedCurveNames(
        FanControlConfig config, Dictionary<string, List<ControlMatch>> boundControls)
    {
        var referenced = new HashSet<string>(boundControls.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var curve in config.Curves)
        {
            foreach (var member in curve.MixCurveNames)
            {
                referenced.Add(member);
            }
        }
        return referenced;
    }

    private static void Unsupported(FanControlCurvePreviewDto preview, string code, string? detail = null)
    {
        preview.Supported = false;
        preview.ReasonCode = code;
        preview.ReasonDetail = detail;
        preview.TargetType = "";
    }

    private static string? Nickname(FanControlControl control) =>
        !string.IsNullOrWhiteSpace(control.NickName)
        && !string.Equals(control.NickName, control.Name, StringComparison.Ordinal)
            ? control.NickName
            : null;

    private static string? TargetType(string kind) => kind switch
    {
        FanControlCurveKinds.Flat => "Flat",
        FanControlCurveKinds.Graph => "Graph",
        FanControlCurveKinds.Linear => "Linear",
        FanControlCurveKinds.Mix => "Mixed",
        FanControlCurveKinds.Trigger => "Trigger",
        FanControlCurveKinds.Sync => "Sync",
        FanControlCurveKinds.Auto => "Auto",
        _ => null,
    };

    private static string TierName(IdentifierMatchTier tier) => tier switch
    {
        IdentifierMatchTier.Exact => "exact",
        IdentifierMatchTier.Normalized => "normalized",
        IdentifierMatchTier.Name => "name",
        IdentifierMatchTier.Ordinal => "position",
        _ => "none",
    };

    /// <summary>Stable, readable curve id derived from the FanControl name.</summary>
    internal static string UniqueId(string name, HashSet<string> used)
    {
        var slug = new StringBuilder("fc-");
        foreach (var ch in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                slug.Append(ch);
            }
            else if (slug[^1] != '-')
            {
                slug.Append('-');
            }
        }
        var candidate = slug.ToString().Trim('-');
        if (candidate.Length <= 3)
        {
            candidate = "fc-curve";
        }

        var unique = candidate;
        var n = 2;
        while (!used.Add(unique))
        {
            unique = candidate + "-" + n.ToString(CultureInfo.InvariantCulture);
            n++;
        }
        return unique;
    }
}
