using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Migration.FanControl;

/// <summary>How confidently an imported identifier was matched to a local one.</summary>
internal enum IdentifierMatchTier
{
    None = 0,
    /// <summary>Byte-identical LibreHardwareMonitor identifier.</summary>
    Exact = 1,
    /// <summary>Same identifier once zero-index segments are ignored.</summary>
    Normalized = 2,
    /// <summary>Same hardware-reported sensor name, and only one candidate had it.</summary>
    Name = 3,
    /// <summary>Paired by position within the same hardware class, when neither identifier nor name could bridge it.</summary>
    Ordinal = 4,
}

/// <summary>
/// Matches FanControl's LibreHardwareMonitor identifiers onto ours.
///
/// Both apps address hardware by LHM identifier, but FanControl ships its own
/// (older) LHM build, and the identifier format changed: a super-IO chip gained
/// an instance segment, so the same motherboard fan is
/// <c>/lpc/it8696e/control/0</c> there and <c>/lpc/it8696e/0/control/0</c> here
/// (measured on the same board). Only segments equal to "0" are dropped when
/// normalizing, so a second GPU or a second chip still cannot collide with the
/// first; anything still ambiguous is reported unmatched rather than guessed.
/// </summary>
internal static class LhmIdentifierMatcher
{
    /// <summary>Identifier with zero-index segments removed, for cross-version comparison.</summary>
    public static string Normalize(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return "";
        }

        var segments = identifier.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(segments.Length);
        for (var i = 0; i < segments.Length; i++)
        {
            // The trailing segment is the sensor index: "0" there is a real
            // discriminator (control/0 vs control/1), never noise.
            if (segments[i] == "0" && i != segments.Length - 1)
            {
                continue;
            }
            kept.Add(segments[i]);
        }
        return "/" + string.Join('/', kept);
    }

    /// <summary>A local channel or sensor an imported identifier can bind to.</summary>
    public readonly record struct Candidate(string Id, string Name);

    /// <summary>
    /// Best local match for a FanControl identifier. <paramref name="sourceName"/>
    /// is FanControl's hardware-reported Name (not the user's nickname), used
    /// only as a last resort and only when it is unique.
    /// </summary>
    public static (string? Id, IdentifierMatchTier Tier) Match(
        string identifier, IReadOnlyList<Candidate> candidates, string? sourceName = null)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return (null, IdentifierMatchTier.None);
        }

        foreach (var c in candidates)
        {
            if (string.Equals(c.Id, identifier, StringComparison.OrdinalIgnoreCase))
            {
                return (c.Id, IdentifierMatchTier.Exact);
            }
        }

        var normalized = Normalize(identifier);
        string? single = null;
        var count = 0;
        foreach (var c in candidates)
        {
            if (string.Equals(Normalize(c.Id), normalized, StringComparison.OrdinalIgnoreCase))
            {
                single = c.Id;
                count++;
            }
        }
        if (count == 1)
        {
            return (single, IdentifierMatchTier.Normalized);
        }

        if (!string.IsNullOrWhiteSpace(sourceName))
        {
            single = null;
            count = 0;
            foreach (var c in candidates)
            {
                if (string.Equals(c.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                {
                    single = c.Id;
                    count++;
                }
            }
            if (count == 1)
            {
                return (single, IdentifierMatchTier.Name);
            }
        }

        return (null, IdentifierMatchTier.None);
    }

    /// <summary>
    /// True for an identifier naming a GPU fan on either side. FanControl drives
    /// NVIDIA fans through its own NvAPI plugin
    /// (<c>NVApiWrapper/0-GA102-A/control/0</c>) and AMD ones through ADLX, so
    /// those identifiers share nothing with the LHM ones we use
    /// (<c>/gpu-nvidia/0/control/1</c>) and no amount of normalizing bridges them.
    /// </summary>
    public static bool IsGpu(string identifier) =>
        identifier.StartsWith("NVApiWrapper/", StringComparison.OrdinalIgnoreCase)
        || identifier.StartsWith("AdlxWrapper/", StringComparison.OrdinalIgnoreCase)
        || identifier.Contains("/gpu-", StringComparison.OrdinalIgnoreCase)
        || identifier.StartsWith("/gpu", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pairs leftover GPU fans by position, which is the only bridge between
    /// the two GPU identifier namespaces. Only when both sides report the same
    /// number of them, so a machine whose GPU fans we enumerate differently
    /// reports them unmatched rather than binding a curve to the wrong fan.
    /// </summary>
    public static Dictionary<string, string> PairGpuByPosition(
        IReadOnlyList<string> unmatchedSources, IReadOnlyList<Candidate> freeCandidates)
    {
        var sources = unmatchedSources.Where(IsGpu).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var targets = freeCandidates.Where(c => IsGpu(c.Id))
            .Select(c => c.Id)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (sources.Count == 0 || sources.Count != targets.Count)
        {
            return pairs;
        }
        for (var i = 0; i < sources.Count; i++)
        {
            pairs[sources[i]] = targets[i];
        }
        return pairs;
    }
}
