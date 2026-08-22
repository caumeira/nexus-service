using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting;

/// <summary>
/// One device's Static assignment: the effect key plus the exact look it was
/// assigned with. Stored whole rather than reduced to a colour, so patterns
/// (gradients, two-tone, spectrum) and the tint controls (hue shift, warmth,
/// contrast) reach the hardware instead of being flattened to one swatch.
/// </summary>
public sealed class StaticDeviceAssignment
{
    public string Effect { get; init; } = "";
    public float Intensity { get; init; } = 1f;
    public float Hue { get; init; }
    public float Colorize { get; init; }
    public float Saturation { get; init; } = 1f;
    public float Contrast { get; init; } = 1f;
    public IReadOnlyDictionary<string, float>? Params { get; init; }

    /// <summary>
    /// Identity of the rendered look. Two devices sharing this share one render,
    /// and a change to any control invalidates the cache.
    /// </summary>
    public string Key()
    {
        var sb = new System.Text.StringBuilder(Effect);
        sb.Append('|').Append(Intensity.ToString("R")).Append('|').Append(Hue.ToString("R"))
          .Append('|').Append(Colorize.ToString("R")).Append('|').Append(Saturation.ToString("R"))
          .Append('|').Append(Contrast.ToString("R"));
        if (Params is not null)
        {
            // Ordered, so two equal looks never hash differently.
            var keys = new List<string>(Params.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var k in keys) sb.Append('|').Append(k).Append('=').Append(Params[k].ToString("R"));
        }
        return sb.ToString();
    }
}

/// <summary>
/// Per-device Static assignments.
///
/// Static mode lets a device wear its own effect instead of the shared canvas.
/// The engine is the only place that can honour that for every contributor at
/// once (OpenRGB zones, first-party hubs and smart lights all funnel through
/// its sampling step), so assignments land here and the engine reads them.
/// Same shape as the identify trackers: a shared singleton, so no DI cycle
/// forms between the routes and the engine.
///
/// Assignments only apply while Static is running - every other mode drives all
/// devices from one source, so <see cref="Enabled"/> gates them off.
/// </summary>
public sealed class StaticDeviceEffectTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<string, StaticDeviceAssignment> _assignments = new(StringComparer.Ordinal);

    /// <summary>True while Static owns the output; false in every other mode.</summary>
    public bool Enabled { get; set; }

    /// <summary>Bumped on every change, so the engine can drop stale renders.</summary>
    public int Version { get; private set; }

    public void Set(string id, StaticDeviceAssignment assignment)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(assignment.Effect)) return;
        lock (_lock) { _assignments[id] = assignment; Version++; }
    }

    public void Clear(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_lock) { if (_assignments.Remove(id)) Version++; }
    }

    public void ClearAll()
    {
        lock (_lock) { if (_assignments.Count > 0) { _assignments.Clear(); Version++; } }
    }

    public bool TryGet(string id, out StaticDeviceAssignment assignment)
    {
        if (!Enabled || string.IsNullOrEmpty(id)) { assignment = null!; return false; }
        lock (_lock) return _assignments.TryGetValue(id, out assignment!);
    }

    public bool Any
    {
        get { if (!Enabled) return false; lock (_lock) return _assignments.Count > 0; }
    }
}
