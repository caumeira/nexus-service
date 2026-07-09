using System;
using System.Collections.Generic;

namespace Nexus.Service.Diagnostics.Temperature;

/// <summary>
/// Pure, tick-driven bucket accumulator; the bucket width is whatever the
/// caller aligns bucketStartMs to, this class has no notion of it. No I/O -
/// callers feed periodic (id, kind, name, valueC) readings via
/// <see cref="Advance"/> alongside the reading's aligned bucket start, and
/// get back any buckets that just rolled over and are ready to persist.
/// Mirrors CoolingStallDetector's pure, hardware-agnostic shape so it is
/// fully unit-testable without live sensors.
/// </summary>
public sealed class TemperatureBucketAccumulator
{
    private readonly Dictionary<string, State> _components = new(StringComparer.Ordinal);

    private sealed class State
    {
        public string Kind = "";
        public string Name = "";
        public long BucketStartMs;
        public double Sum;
        public double Max;
        public int Count;
    }

    /// <summary>
    /// Rolls over any tracked component whose bucket has passed - even one
    /// absent from <paramref name="readings"/> this call, e.g. a drive that
    /// dropped out - then accumulates every reading into bucketStartMs.
    /// Returns the buckets closed by the rollover.
    /// </summary>
    public IReadOnlyList<TemperatureBucketRow> Advance(
        long bucketStartMs, IEnumerable<(string Id, string Kind, string Name, double ValueC)> readings)
    {
        var flushed = new List<TemperatureBucketRow>();

        foreach (var (id, state) in _components)
        {
            if (state.Count > 0 && state.BucketStartMs != bucketStartMs)
            {
                flushed.Add(new TemperatureBucketRow(
                    id, state.Kind, state.Name, state.BucketStartMs, state.Sum / state.Count, state.Max, state.Count));
                state.Count = 0;
                state.Sum = 0;
                state.Max = 0;
            }
            state.BucketStartMs = bucketStartMs;
        }

        foreach (var (id, kind, name, valueC) in readings)
        {
            if (!_components.TryGetValue(id, out var state))
            {
                state = new State { BucketStartMs = bucketStartMs };
                _components[id] = state;
            }
            state.Kind = kind;
            state.Name = name;
            // Max seeds from the first sample of the bucket, not 0 - a bucket
            // whose readings are all negative must not report Max as 0.
            state.Max = state.Count == 0 ? valueC : Math.Max(state.Max, valueC);
            state.Sum += valueC;
            state.Count++;
        }

        return flushed;
    }
}
