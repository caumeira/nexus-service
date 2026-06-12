using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Service.Lighting.Mappings;

/// <summary>
/// Canonical content hashing for mapping artifacts.
///
/// CROSS-LANGUAGE CONTRACT - must stay byte-identical with the registry's
/// artifact-hash.ts. The hash input is a flat string (never JSON) built from
/// the FUNCTIONAL fields only; cosmetic metadata (name, description, author)
/// is excluded so renames do not change identity:
///
///   "nexusmap:v1" then per zone ordered by ascending zoneIndex:
///     "|z{zoneIndex}|c{ledCount or -1}|a{aspect}"
///        aspect = aspectRatio fixed to 4 decimals, or "-1" when null
///     "|L" + leds sorted by ascending index as "{i}:{u}:{v}" joined by ","
///        u/v clamped to [0,1], fixed to 4 decimals, never exponent form
///     "|D" + ascending disabled indices joined by ","
///     "|G" + groups sorted by trimmed name (ordinal), each
///        "{name}={ranges}" with ascending "a-b" pairs joined by "+",
///        groups joined by ";"
///
///   contentHash  = lowercase sha256 hex of that string.
///   clusterHash  = same construction with u/v fixed to 2 decimals, aspect
///                  to 1 decimal, and the "|G" section omitted entirely -
///                  near-identical mappings (drag jitter) cluster together.
/// </summary>
public static class MappingHash
{
    public static string ContentHash(MappingArtifact artifact)
        => Sha256Hex(CanonicalString(artifact, uvDecimals: 4, aspectDecimals: 4, includeGroups: true));

    public static string ClusterHash(MappingArtifact artifact)
        => Sha256Hex(CanonicalString(artifact, uvDecimals: 2, aspectDecimals: 1, includeGroups: false));

    internal static string CanonicalString(MappingArtifact artifact, int uvDecimals, int aspectDecimals, bool includeGroups)
    {
        var sb = new StringBuilder();
        sb.Append("nexusmap:v1");

        var zones = new List<MappingZone>(artifact.Zones);
        zones.Sort((a, b) => a.ZoneIndex.CompareTo(b.ZoneIndex));

        foreach (var zone in zones)
        {
            sb.Append("|z").Append(zone.ZoneIndex.ToString(CultureInfo.InvariantCulture));
            sb.Append("|c").Append((zone.LedCount ?? -1).ToString(CultureInfo.InvariantCulture));
            sb.Append("|a").Append(zone.AspectRatio is { } ar
                ? Fixed(ar, aspectDecimals)
                : "-1");

            sb.Append("|L");
            var leds = new List<MappingLed>(zone.Leds);
            leds.Sort((a, b) => a.I.CompareTo(b.I));
            for (int i = 0; i < leds.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var led = leds[i];
                sb.Append(led.I.ToString(CultureInfo.InvariantCulture))
                  .Append(':').Append(Fixed(Clamp01(led.U), uvDecimals))
                  .Append(':').Append(Fixed(Clamp01(led.V), uvDecimals));
            }

            sb.Append("|D");
            var disabled = new List<int>(zone.Disabled);
            disabled.Sort();
            for (int i = 0; i < disabled.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(disabled[i].ToString(CultureInfo.InvariantCulture));
            }

            if (includeGroups)
            {
                sb.Append("|G");
                var groups = new List<MappingGroup>(zone.Groups);
                groups.Sort((a, b) => string.CompareOrdinal(a.Name.Trim(), b.Name.Trim()));
                for (int g = 0; g < groups.Count; g++)
                {
                    if (g > 0) sb.Append(';');
                    sb.Append(groups[g].Name.Trim()).Append('=');
                    var ranges = new List<MappingLedRange>(groups[g].Ranges);
                    ranges.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
                    for (int r = 0; r < ranges.Count; r++)
                    {
                        if (r > 0) sb.Append('+');
                        sb.Append(ranges[r].Start.ToString(CultureInfo.InvariantCulture))
                          .Append('-')
                          .Append(ranges[r].End.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
        }
        return sb.ToString();
    }

    private static float Clamp01(float v) => float.IsNaN(v) ? 0f : Math.Clamp(v, 0f, 1f);

    // Away-from-zero matches JS toFixed; banker's rounding would diverge on ties.
    private static string Fixed(float value, int decimals)
        => Math.Round((double)value, decimals, MidpointRounding.AwayFromZero)
            .ToString("F" + decimals, CultureInfo.InvariantCulture);

    private static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
