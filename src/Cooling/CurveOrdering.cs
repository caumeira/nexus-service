using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Cooling;

/// <summary>
/// Dependency order for curve evaluation. Mixed curves read their member
/// curves' output and Sync curves read a fan channel another curve may drive,
/// so both have to be evaluated after what they depend on. Input order is kept
/// among independent curves so the evaluation order is stable.
/// </summary>
internal static class CurveOrdering
{
    /// <summary>
    /// Curve ids each curve depends on. A Sync curve depends on whichever curve
    /// drives its source channel (none, when the channel is manual or on BIOS).
    /// </summary>
    internal static Dictionary<string, HashSet<string>> BuildEdges(IReadOnlyList<CurveDocument> curves)
    {
        var curveByOutput = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in curves)
        {
            foreach (var o in c.Outputs)
            {
                if (!string.IsNullOrEmpty(o.Id))
                {
                    curveByOutput[o.Id] = c.Id;
                }
            }
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in curves)
        {
            ids.Add(c.Id);
        }

        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var c in curves)
        {
            var deps = new HashSet<string>(StringComparer.Ordinal);
            if (c.Type == "Mixed" && c.Mixed is not null)
            {
                foreach (var id in c.Mixed.CurveIds)
                {
                    if (id != c.Id && ids.Contains(id))
                    {
                        deps.Add(id);
                    }
                }
            }
            else if (c.Type == "Sync" && c.Sync is not null
                     && curveByOutput.TryGetValue(c.Sync.SourceChannelId, out var driver))
            {
                // Following a channel this curve itself drives is a one-curve
                // cycle: its offset would compound against its own last output
                // every tick. Recorded as a self-dependency so it is reported
                // like any other cycle.
                deps.Add(driver);
            }
            edges[c.Id] = deps;
        }
        return edges;
    }

    /// <summary>
    /// Curves in dependency order. Any curve caught in a cycle is appended
    /// after the resolved ones, so it still evaluates (against last tick's
    /// values) instead of vanishing.
    /// </summary>
    public static List<CurveDocument> Sort(IReadOnlyList<CurveDocument> curves)
    {
        var edges = BuildEdges(curves);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<CurveDocument>(curves.Count);

        bool progressed;
        do
        {
            progressed = false;
            foreach (var c in curves)
            {
                if (emitted.Contains(c.Id))
                {
                    continue;
                }
                var ready = true;
                foreach (var dep in edges[c.Id])
                {
                    if (!emitted.Contains(dep))
                    {
                        ready = false;
                        break;
                    }
                }
                if (ready)
                {
                    ordered.Add(c);
                    emitted.Add(c.Id);
                    progressed = true;
                }
            }
        } while (progressed && ordered.Count < curves.Count);

        foreach (var c in curves)
        {
            if (!emitted.Contains(c.Id))
            {
                ordered.Add(c);
            }
        }
        return ordered;
    }

    /// <summary>Ids of curves that sit in a dependency cycle. Empty when the graph is a DAG.</summary>
    public static List<string> FindCycleMembers(IReadOnlyList<CurveDocument> curves)
    {
        var ordered = Sort(curves);
        var edges = BuildEdges(curves);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var cyclic = new List<string>();

        foreach (var c in ordered)
        {
            var ready = true;
            foreach (var dep in edges[c.Id])
            {
                if (!emitted.Contains(dep))
                {
                    ready = false;
                    break;
                }
            }
            if (ready)
            {
                emitted.Add(c.Id);
            }
            else
            {
                cyclic.Add(c.Id);
            }
        }
        return cyclic;
    }
}
