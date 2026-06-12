using System;
using System.Collections.Generic;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Mappings;

/// <summary>
/// Layout state for frames contributed by non-OpenRGB providers (NP50,
/// hubs, smart lights). Providers may author their own per-LED UV defaults
/// (smart-light sample grids); this tracker snapshots those pristine
/// defaults - recognizing our own previously-applied arrays by reference so
/// they are never mistaken for provider data - and re-applies the resolver
/// stack (defaults -> applied mapping -> user deltas) on every frame
/// rebuild. Without it, contributor frames lose their custom layouts on any
/// topology refresh, and a partial user edit would clobber provider UVs.
/// </summary>
public sealed class ContributorFrameLayouts
{
    private sealed class Entry
    {
        public float[]? DefaultU;
        public float[]? DefaultV;
        public float[]? LastAppliedU;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new();

    /// <summary>
    /// <paramref name="overrides"/> maps a partition-backed card (keeb zone)
    /// into its device's segment-local override space; null treats the card
    /// as a single-segment device of its own. <paramref name="seedU"/> /
    /// <paramref name="seedV"/> carry structure-authored segment defaults for
    /// the frame's zone; they are authoritative over any frame-authored UVs
    /// (a reused frame instance can survive a partition reshape whose seed
    /// changed) and become the pristine baseline.
    /// </summary>
    public void Refresh(DeviceFrame frame, NexusSettings settings,
        Nexus.Service.Lighting.Zones.ZoneOverrideContext? overrides = null,
        float[]? seedU = null, float[]? seedV = null)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(frame.Id, out var entry))
            {
                entry = new Entry();
                _entries[frame.Id] = entry;
            }

            if (seedU is not null && seedV is not null
                && seedU.Length == frame.LedCount && seedV.Length == frame.LedCount)
            {
                entry.DefaultU = (float[])seedU.Clone();
                entry.DefaultV = (float[])seedV.Clone();
            }
            // Provider-authored UVs become the pristine default. Arrays we
            // applied ourselves are recognized by reference and skipped.
            else if (frame.LedU is not null && frame.LedV is not null
                && !ReferenceEquals(frame.LedU, entry.LastAppliedU)
                && frame.LedU.Length == frame.LedCount
                && frame.LedV.Length == frame.LedCount)
            {
                entry.DefaultU = (float[])frame.LedU.Clone();
                entry.DefaultV = (float[])frame.LedV.Clone();
            }
            if (entry.DefaultU is not null && entry.DefaultU.Length != frame.LedCount)
            {
                entry.DefaultU = null;
                entry.DefaultV = null;
            }

            var resolved = LedLayoutResolver.ResolveSeeded(
                frame.Id, frame.LedCount, entry.DefaultU, entry.DefaultV, settings, overrides);
            LedLayoutResolver.ApplyToFrame(frame, resolved);
            entry.LastAppliedU = frame.LedU;
        }
    }

    /// <summary>
    /// Route-side frame application for contributor devices. Every write of
    /// resolver output into a tracked frame MUST go through here (not
    /// LedLayoutResolver.ApplyToFrame directly): the tracker recognizes its
    /// own arrays by reference, so an untracked write would be mistaken for
    /// provider-authored defaults on the next bridge rebuild and permanently
    /// contaminate the reset baseline with user/mapping state.
    /// </summary>
    public void Apply(DeviceFrame frame, ResolvedLedLayout resolved)
    {
        lock (_lock)
        {
            LedLayoutResolver.ApplyToFrame(frame, resolved);
            if (!_entries.TryGetValue(frame.Id, out var entry))
            {
                entry = new Entry();
                _entries[frame.Id] = entry;
            }
            entry.LastAppliedU = frame.LedU;
        }
    }

    /// <summary>Pristine provider defaults for the editor's reset baseline; null when the provider never authored UVs (linear default applies).</summary>
    public (float[]? U, float[]? V) GetDefaults(string id)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(id, out var entry)
                ? (entry.DefaultU, entry.DefaultV)
                : (null, null);
        }
    }

    /// <summary>Drop state for devices no longer present so removed hardware does not pin arrays forever.</summary>
    public void Prune(IReadOnlyCollection<string> liveIds)
    {
        lock (_lock)
        {
            if (_entries.Count == 0)
                return;
            List<string>? stale = null;
            foreach (var key in _entries.Keys)
            {
                var live = false;
                foreach (var id in liveIds)
                {
                    if (string.Equals(id, key, StringComparison.Ordinal))
                    { live = true; break; }
                }
                if (!live)
                    (stale ??= new List<string>()).Add(key);
            }
            if (stale is not null)
            {
                foreach (var key in stale)
                    _entries.Remove(key);
            }
        }
    }
}
