using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;

namespace Nexus.Service.Migration.FanControl;

/// <summary>
/// Reads a FanControl configuration and imports it: curves (including the
/// trigger, sync and auto modes ported for this), the fans they drive, the
/// calibration tables FanControl already measured, nicknames, per-fan offsets
/// and manual duties.
///
/// Curves this import created are tagged by an <c>fc-</c> id prefix, so
/// re-importing replaces them instead of stacking duplicates. Curves the user
/// made here are never touched.
/// </summary>
public sealed class FanControlImportService
{
    /// <summary>Id prefix marking a curve this import owns.</summary>
    internal const string ImportedIdPrefix = "fc-";

    public const string CategoryCurves = "curves";
    public const string CategoryCalibration = "calibration";
    public const string CategoryNames = "names";
    public const string CategoryOffsets = "offsets";
    public const string CategoryManual = "manual";

    private readonly IFanControlDetector _detector;
    private readonly IFanControlProvider _fans;
    private readonly IConfigStore _store;

    public FanControlImportService(IFanControlDetector detector, IFanControlProvider fans, IConfigStore store)
    {
        _detector = detector;
        _fans = fans;
        _store = store;
    }

    public FanControlPreviewResponse Preview(string? configPath)
    {
        var plan = BuildPlan(configPath, out var error);
        if (plan is null)
        {
            return new FanControlPreviewResponse { Available = false, Error = true, Msg = error };
        }
        return plan.Preview;
    }

    public FanControlApplyResponse Apply(string? configPath, IReadOnlyList<string> categories)
    {
        var plan = BuildPlan(configPath, out var error);
        if (plan is null)
        {
            return new FanControlApplyResponse { Error = true, Msg = error };
        }

        var wanted = new HashSet<string>(categories, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return new FanControlApplyResponse { Error = true, Msg = "No categories selected" };
        }

        var response = new FanControlApplyResponse();

        _store.Update(s =>
        {
            if (wanted.Contains(CategoryCurves))
            {
                var importedChannels = new HashSet<string>(
                    plan.Curves.SelectMany(c => c.Outputs).Select(o => o.Id), StringComparer.Ordinal);

                // Drop the previous import, then release the channels this one
                // claims from any user curve still driving them: two curves on
                // one channel would fight every tick.
                s.Cooling.Curves.RemoveAll(c => c.Id.StartsWith(ImportedIdPrefix, StringComparison.Ordinal));
                foreach (var existing in s.Cooling.Curves)
                {
                    existing.Outputs.RemoveAll(o => importedChannels.Contains(o.Id));
                }

                s.Cooling.Curves.AddRange(plan.Curves);

                // A fan on a curve has no manual duty; leaving one behind would
                // let the engine's replay fight the curve.
                foreach (var id in importedChannels)
                {
                    s.Cooling.ManualSpeeds.Remove(id);
                }
                response.CurvesImported = plan.Curves.Count;
            }

            if (wanted.Contains(CategoryCalibration))
            {
                foreach (var (id, calibration) in plan.Calibrations)
                {
                    s.Cooling.FanCalibrations[id] = calibration;
                }
                response.CalibrationsImported = plan.Calibrations.Count;
            }

            if (wanted.Contains(CategoryNames))
            {
                foreach (var (id, name) in plan.Names)
                {
                    s.Cooling.FanNames[id] = name;
                }
                response.NamesImported = plan.Names.Count;
            }

            if (wanted.Contains(CategoryOffsets))
            {
                foreach (var (id, offset) in plan.Offsets)
                {
                    s.Cooling.FanOffsets[id] = offset;
                }
                response.OffsetsImported = plan.Offsets.Count;
            }

            if (wanted.Contains(CategoryManual))
            {
                foreach (var (id, duty) in plan.ManualSpeeds)
                {
                    // A channel this import put on a curve must not also carry
                    // a manual duty.
                    if (s.Cooling.Curves.Any(c => c.Outputs.Any(o => o.Id == id)))
                    {
                        continue;
                    }
                    s.Cooling.ManualSpeeds[id] = duty;
                    response.ManualImported++;
                }
            }

            s.FanControlImportCompleted = true;
            s.FanControlImportOffered = true;
        });

        // Fans are on user curves now, so the profile bar has to agree.
        var derived = FanProfiles.DerivePresetFromCurves(_store, _fans);
        _store.Update(s => s.Cooling.ActivePreset = derived);

        return response;
    }

    private FanControlImportPlan? BuildPlan(string? configPath, out string error)
    {
        error = "Ok";
        var detection = _detector.Detect();
        if (detection.Configs.Count == 0)
        {
            error = "No FanControl configuration found";
            return null;
        }

        var config = string.IsNullOrEmpty(configPath)
            ? detection.Configs[0]
            : detection.Configs.FirstOrDefault(c =>
                string.Equals(c.Path, configPath, StringComparison.OrdinalIgnoreCase));
        if (config is null)
        {
            error = "That FanControl configuration no longer exists";
            return null;
        }

        var json = _detector.ReadConfig(config.Path);
        if (string.IsNullOrEmpty(json))
        {
            error = "Could not read the FanControl configuration";
            return null;
        }

        FanControlConfig parsed;
        try
        {
            parsed = FanControlConfigParser.Parse(json);
        }
        catch (Exception ex)
        {
            error = $"Could not read the FanControl configuration: {ex.Message}";
            return null;
        }

        var channels = _fans.GetFanChannels()
            .Select(c => new LhmIdentifierMatcher.Candidate(c.Id, c.Name))
            .ToList();
        var sensors = _fans.GetTemperatureSources()
            .Select(s => new LhmIdentifierMatcher.Candidate(s.Id, s.Name))
            .ToList();

        var plan = FanControlImportMapper.Build(parsed, channels, sensors);

        // Imported curves come from another app's file, so they go through the
        // same clamp as anything posted to /cooling/curves/set rather than
        // straight into the store.
        var sanitized = CoolingSafety.Sanitize(new SetCurvesBody
        {
            GlobalSpeedModifier = 1.0,
            Curves = plan.Curves.ConvertAll(CurveWireMapper.ToWire),
        });
        plan.Curves.Clear();
        plan.Curves.AddRange(sanitized.Curves.ConvertAll(CurveWireMapper.ToDocument));

        plan.Preview.ConfigName = config.Name;
        return plan;
    }
}
