using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.Mappings;

/// <summary>
/// Deterministic artifact validation - the client-side mirror of the
/// registry's publish-time lint. Every artifact is re-validated locally
/// before it touches the engine, regardless of origin (registry, share link,
/// .nexusmap file), so a bad payload degrades to "rejected", never to broken
/// lighting. Returns errors; never throws for content reasons.
/// </summary>
public static class MappingLint
{
    /// <summary>Zones with at least this many LEDs are subject to collapse detection.</summary>
    private const int CollapseCheckMinLeds = 8;
    /// <summary>Max perpendicular deviation (in UV space) below which a many-LED cloud counts as collapsed onto a line.</summary>
    private const float CollinearEpsilon = 0.001f;

    public static MappingLintResult Validate(MappingArtifact? artifact)
        => Validate(artifact, zoneIsLinear: null);

    /// <param name="zoneIsLinear">
    /// Zone-type evidence when available (the live device's zone type or the
    /// artifact's zoneSignature). Collapse detection only rejects colinear
    /// layouts for zones known NOT to be linear strips; with no evidence,
    /// colinear is allowed (strips are the common case).
    /// </param>
    public static MappingLintResult Validate(MappingArtifact? artifact, Func<int, bool>? zoneIsLinear)
    {
        var errors = new List<string>();
        var result = new MappingLintResult(errors);
        if (artifact is null)
        {
            errors.Add("artifact missing");
            return result;
        }
        if (artifact.SchemaVersion != MappingSchema.Version)
        {
            errors.Add($"unsupported schemaVersion {artifact.SchemaVersion}");
            return result;
        }
        if (string.IsNullOrWhiteSpace(artifact.Device.Key))
            errors.Add("device.key missing");
        if (artifact.Name.Length > MappingSchema.MaxNameLength)
            errors.Add("name too long");
        if (artifact.Description is { } d && d.Length > MappingSchema.MaxDescriptionLength)
            errors.Add("description too long");
        if (artifact.Zones.Count == 0)
        {
            errors.Add("zones empty");
            return result;
        }

        var seenZoneIndices = new HashSet<int>();
        foreach (var zone in artifact.Zones)
        {
            var zl = $"zone {zone.ZoneIndex}";
            if (zone.ZoneIndex < 0)
                errors.Add($"{zl}: negative index");
            if (!seenZoneIndices.Add(zone.ZoneIndex))
                errors.Add($"{zl}: duplicate index");
            if (zone.LedCount is { } lc && (lc < 0 || lc > 4096))
                errors.Add($"{zl}: ledCount out of range");
            if (zone.AspectRatio is { } ar && (!float.IsFinite(ar) || ar <= 0))
                errors.Add($"{zl}: invalid aspectRatio");

            if (zone.Leds.Count == 0)
            {
                errors.Add($"{zl}: leds empty");
                continue;
            }

            var bound = zone.LedCount ?? int.MaxValue;
            var seenLeds = new HashSet<int>();
            foreach (var led in zone.Leds)
            {
                if (led.I < 0 || led.I >= bound)
                    errors.Add($"{zl}: led index {led.I} out of bounds");
                if (!seenLeds.Add(led.I))
                    errors.Add($"{zl}: duplicate led index {led.I}");
                if (!float.IsFinite(led.U) || led.U < 0f || led.U > 1f
                    || !float.IsFinite(led.V) || led.V < 0f || led.V > 1f)
                {
                    errors.Add($"{zl}: led {led.I} uv out of range");
                }
            }

            var seenDisabled = new HashSet<int>();
            foreach (var di in zone.Disabled)
            {
                if (di < 0 || di >= bound)
                    errors.Add($"{zl}: disabled index {di} out of bounds");
                if (!seenDisabled.Add(di))
                    errors.Add($"{zl}: duplicate disabled index {di}");
            }

            if (zone.Groups.Count > MappingSchema.MaxGroupsPerZone)
                errors.Add($"{zl}: too many groups");
            var seenGroupNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in zone.Groups)
            {
                var name = group.Name.Trim();
                if (name.Length == 0 || name.Length > MappingSchema.MaxGroupNameLength)
                { errors.Add($"{zl}: invalid group name"); continue; }
                if (!seenGroupNames.Add(name))
                    errors.Add($"{zl}: duplicate group name '{name}'");
                if (group.Ranges.Count == 0)
                    errors.Add($"{zl}: group '{name}' empty");
                foreach (var range in group.Ranges)
                {
                    if (range.Start < 0 || range.End < range.Start || range.End >= bound)
                        errors.Add($"{zl}: group '{name}' range invalid");
                }
            }

            if (errors.Count == 0)
                CheckCollapse(zone, errors, result, zoneIsLinear);
        }
        return result;
    }

    /// <summary>
    /// Geometry sanity for shareable layouts. A many-LED zone where every
    /// position is identical, or the whole cloud sits on one line, is almost
    /// certainly garbage - UNLESS the zone is declared linear (strips are
    /// legitimately colinear). A mapping that disables most of its LEDs is
    /// valid locally (hidden fan) but flagged so it never auto-applies.
    /// </summary>
    private static void CheckCollapse(MappingZone zone, List<string> errors, MappingLintResult result, Func<int, bool>? zoneIsLinear)
    {
        if (zone.Disabled.Count * 2 > Math.Max(1, zone.Leds.Count))
            result.AutoApplyIneligible = true;

        if (zone.Leds.Count < CollapseCheckMinLeds)
            return;

        float minU = 1f, maxU = 0f, minV = 1f, maxV = 0f;
        foreach (var led in zone.Leds)
        {
            minU = Math.Min(minU, led.U); maxU = Math.Max(maxU, led.U);
            minV = Math.Min(minV, led.V); maxV = Math.Max(maxV, led.V);
        }
        var spanU = maxU - minU;
        var spanV = maxV - minV;
        if (spanU < CollinearEpsilon && spanV < CollinearEpsilon)
        {
            errors.Add($"zone {zone.ZoneIndex}: all led positions identical");
            return;
        }

        // With no zone-type evidence, colinear must be allowed - strips are
        // the common case and rejecting them would block legitimate layouts.
        if (zoneIsLinear is null || zoneIsLinear(zone.ZoneIndex))
            return;

        // Max perpendicular deviation from the dominant-axis line through the
        // cloud's extremes; a 2D layout collapsed onto any straight line is
        // flagged. Axis-aligned and diagonal lines are both caught.
        var dx = spanU;
        var dy = spanV;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len <= 0f)
            return;
        var maxDeviation = 0f;
        foreach (var led in zone.Leds)
        {
            var deviation = MathF.Abs(dx * (led.V - minV) - dy * (led.U - minU)) / len;
            maxDeviation = MathF.Max(maxDeviation, deviation);
        }
        if (maxDeviation < CollinearEpsilon)
            errors.Add($"zone {zone.ZoneIndex}: led positions collapsed onto a line");
    }

}

public sealed class MappingLintResult
{
    public MappingLintResult(List<string> errors) { Errors = errors; }
    public List<string> Errors { get; }
    public bool Ok => Errors.Count == 0;
    /// <summary>Valid but should never auto-apply as a community default (e.g. most LEDs disabled).</summary>
    public bool AutoApplyIneligible { get; set; }
}
