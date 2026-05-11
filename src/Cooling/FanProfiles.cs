using System.Collections.Generic;
using System.Linq;
using Qos.Service.Models.Cooling;
using Qos.Service.Persistence;

namespace Qos.Service.Cooling;

/// <summary>
/// Built-in fan presets: Off, Silent, Balanced, Performance, Custom.
///
/// Applying Silent / Balanced / Performance ensures a single shared "preset
/// curve" exists with id `preset-{name}`, attaches every fan to it, and
/// detaches those fans from any user curve. User curves are NOT deleted.
///
/// Applying Custom restores the last-known per-fan curve assignment that was
/// active before a preset took over.
///
/// Applying Off detaches every fan from every curve and releases every fan
/// to BIOS Control.
///
/// The preset curve's parameters survive round-trips: re-applying a preset
/// after the user edits its MinTemp/MaxTemp/etc. keeps the user's edits.
/// Deleting a preset curve is allowed; the next activation recreates it
/// with the default values from PresetDefaults.
/// </summary>
public static class FanProfiles
{
    public static List<FanProfile> GetBuiltInProfiles() => new()
    {
        new FanProfile { Name = "off", Description = "All fans released to BIOS Control" },
        new FanProfile { Name = "silent", Description = "Quiet operation - fans stay low until temperatures demand it" },
        new FanProfile { Name = "balanced", Description = "Moderate cooling - responsive but not aggressive" },
        new FanProfile { Name = "performance", Description = "Maximum cooling - fans run fast to keep temperatures low" },
        new FanProfile { Name = "custom", Description = "User-defined per-fan curve assignments" },
    };

    /// <summary>
    /// Apply the named preset. Returns the canonical preset name actually applied.
    /// "auto" is treated as a synonym for "off" for backward compatibility.
    /// </summary>
    public static string Apply(string profileName, IFanControlProvider fans, IConfigStore store)
    {
        var canonical = Canonicalize(profileName);
        var channels = fans.GetFanChannels();
        var temps = fans.GetTemperatureSources();
        var inputSensor = PreferredInput(temps);
        var fanIds = channels.Select(c => c.Id).ToHashSet();

        store.Update(s =>
        {
            // Snapshot the user's custom mapping on the way out of "custom".
            if (s.Cooling.ActivePreset == "custom" && canonical != "custom")
            {
                s.Cooling.CustomFanCurveAssignments = SnapshotMapping(s.Cooling.Curves, fanIds);
            }

            switch (canonical)
            {
                case "silent":
                case "balanced":
                case "performance":
                    {
                        var presetCurve = EnsurePresetCurve(s.Cooling.Curves, canonical, inputSensor);
                        // Detach all fans from non-preset curves so the preset curve owns them.
                        foreach (var curve in s.Cooling.Curves)
                        {
                            if (curve.Preset is null && curve.Id != presetCurve.Id)
                            {
                                curve.Outputs.RemoveAll(o => fanIds.Contains(o.Id));
                            }
                            else if (curve.Preset is not null && curve.Id != presetCurve.Id)
                            {
                                // Other preset curves (not the active one) hold no outputs.
                                curve.Outputs.Clear();
                            }
                        }
                        presetCurve.Outputs = channels
                            .Select(c => new CurveOutputDocument { Id = c.Id, Type = "Fan" })
                            .ToList();
                        s.Cooling.ActivePreset = canonical;
                        break;
                    }
                case "custom":
                    {
                        // Detach every preset curve.
                        foreach (var curve in s.Cooling.Curves.Where(c => c.Preset is not null))
                        {
                            curve.Outputs.Clear();
                        }
                        // Restore the saved custom mapping. Any fan missing from the
                        // snapshot stays unattached -> falls through to BIOS Control.
                        var snapshot = s.Cooling.CustomFanCurveAssignments ?? new Dictionary<string, string>();
                        // First clear every non-preset curve's outputs for fans we know about,
                        // so we don't leave stale attachments from a prior state.
                        foreach (var curve in s.Cooling.Curves.Where(c => c.Preset is null))
                        {
                            curve.Outputs.RemoveAll(o => fanIds.Contains(o.Id));
                        }
                        foreach (var (fanId, curveId) in snapshot)
                        {
                            if (!fanIds.Contains(fanId)) continue;
                            var curve = s.Cooling.Curves.FirstOrDefault(c => c.Id == curveId && c.Preset is null);
                            if (curve is null) continue;
                            if (!curve.Outputs.Any(o => o.Id == fanId))
                            {
                                curve.Outputs.Add(new CurveOutputDocument { Id = fanId, Type = "Fan" });
                            }
                        }
                        s.Cooling.ActivePreset = "custom";
                        break;
                    }
                case "off":
                default:
                    {
                        // Detach every fan from every curve so nothing drives them.
                        foreach (var curve in s.Cooling.Curves)
                        {
                            curve.Outputs.RemoveAll(o => fanIds.Contains(o.Id));
                        }
                        s.Cooling.ActivePreset = "off";
                        break;
                    }
            }
        });

        // Off: also actively release fans at the hardware layer so any prior
        // manual override stops holding the duty.
        if (canonical == "off")
        {
            foreach (var ch in channels)
            {
                fans.ReleaseFan(ch.Id);
            }
        }

        return canonical;
    }

    /// <summary>
    /// Recompute ActivePreset from the per-fan effective control state. Each
    /// fan resolves to one of: BIOS, Manual, or attached to a specific curve.
    /// Rules:
    ///   - Every fan BIOS (no curve attachment, no manual override) -> "off".
    ///   - Every fan attached to the same `preset-{name}` curve and no manual
    ///     overrides -> that preset.
    ///   - Anything else -> "custom".
    /// "Manual on at least one fan" never resolves to "off" or a preset; the
    /// user explicitly broke out of the shared regime.
    /// </summary>
    public static string DerivePresetFromCurves(IConfigStore store, IFanControlProvider fans)
    {
        var channels = fans.GetFanChannels();
        var fanIds = channels.Select(c => c.Id).ToHashSet();
        if (fanIds.Count == 0) return "custom";

        var settings = store.Load();
        var curves = settings.Cooling.Curves;
        var attachment = new Dictionary<string, string>(); // fanId -> curveId
        foreach (var curve in curves)
        {
            foreach (var o in curve.Outputs)
            {
                if (fanIds.Contains(o.Id) && !attachment.ContainsKey(o.Id))
                {
                    attachment[o.Id] = curve.Id;
                }
            }
        }
        var manualSet = settings.Cooling.ManualSpeeds.Keys.Where(fanIds.Contains).ToHashSet();

        // All fans BIOS = no attachment AND no manual override on any fan.
        if (attachment.Count == 0 && manualSet.Count == 0) return "off";

        // All fans on the same preset curve, with no manual overrides.
        if (manualSet.Count == 0)
        {
            foreach (var presetName in new[] { "silent", "balanced", "performance" })
            {
                var presetId = $"preset-{presetName}";
                if (fanIds.All(id => attachment.TryGetValue(id, out var cid) && cid == presetId))
                {
                    return presetName;
                }
            }
        }

        return "custom";
    }

    /// <summary>
    /// Snapshot the current per-fan curve assignment. Only fans driven by a
    /// non-preset (user) curve are recorded; fans on BIOS Control or driven
    /// by a preset curve produce no entry.
    /// </summary>
    private static Dictionary<string, string> SnapshotMapping(List<CurveDocument> curves, HashSet<string> fanIds)
    {
        var map = new Dictionary<string, string>();
        foreach (var curve in curves)
        {
            if (curve.Preset is not null) continue;
            foreach (var o in curve.Outputs)
            {
                if (fanIds.Contains(o.Id))
                {
                    map[o.Id] = curve.Id;
                }
            }
        }
        return map;
    }

    private static CurveDocument EnsurePresetCurve(List<CurveDocument> curves, string presetName, TemperatureSource? inputSensor)
    {
        var id = $"preset-{presetName}";
        var existing = curves.FirstOrDefault(c => c.Id == id);
        if (existing is not null)
        {
            // Make sure the Preset flag is set; tolerate older settings written
            // before the field existed.
            if (existing.Preset != presetName) existing.Preset = presetName;
            return existing;
        }
        var defaults = PresetDefaults.For(presetName);
        var doc = new CurveDocument
        {
            Id = id,
            Name = DisplayName(presetName),
            Type = "Linear",
            Preset = presetName,
            Input = inputSensor is null
                ? new CurveInputDocument()
                : new CurveInputDocument { Id = inputSensor.Id, Type = "Temperature", Device = inputSensor.Category },
            Outputs = new List<CurveOutputDocument>(),
            Linear = new LinearCurveData
            {
                ResponseTime = defaults.ResponseTime,
                MinTemp = defaults.MinTemp,
                MaxTemp = defaults.MaxTemp,
                MinSpeed = defaults.MinSpeed,
                MaxSpeed = defaults.MaxSpeed,
            },
        };
        curves.Add(doc);
        return doc;
    }

    private static TemperatureSource? PreferredInput(IReadOnlyList<TemperatureSource> temps)
    {
        return temps.FirstOrDefault(t => t.Category == "CPU" && t.Name.Contains("Package", System.StringComparison.OrdinalIgnoreCase))
            ?? temps.FirstOrDefault(t => t.Category == "CPU")
            ?? temps.FirstOrDefault();
    }

    private static string Canonicalize(string profileName)
    {
        return (profileName ?? "").ToLowerInvariant() switch
        {
            "off" or "auto" => "off",
            "silent" => "silent",
            "balanced" => "balanced",
            "performance" => "performance",
            "custom" => "custom",
            _ => "custom",
        };
    }

    private static string DisplayName(string presetName) => presetName switch
    {
        "silent" => "Silent",
        "balanced" => "Balanced",
        "performance" => "Performance",
        _ => presetName,
    };

    /// <summary>
    /// Default Linear curve parameters per preset. Preset curves are
    /// recreated from these values when the user has deleted them.
    /// </summary>
    public readonly record struct PresetCurveDefaults(double ResponseTime, double MinTemp, double MaxTemp, double MinSpeed, double MaxSpeed);

    public static class PresetDefaults
    {
        public static PresetCurveDefaults For(string presetName) => presetName switch
        {
            "silent" => new(3.0, 45, 85, 20, 70),
            "balanced" => new(1.5, 35, 75, 30, 90),
            "performance" => new(0.5, 30, 65, 50, 100),
            _ => new(1.0, 30, 80, 30, 100),
        };
    }
}
