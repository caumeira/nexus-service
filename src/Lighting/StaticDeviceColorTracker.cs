using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting;

/// <summary>
/// Per-device Static colour assignments.
///
/// Static mode lets a device wear its own colour instead of the shared canvas.
/// The engine is the only place that can honour that for every contributor at
/// once (OpenRGB zones, first-party hubs, smart lights all funnel through
/// <c>ApplyTestOverlays</c>), so the assignment lands here and the engine reads
/// it while sampling. Same shape as the identify trackers: a shared singleton,
/// so no DI cycle forms between the routes and the engine.
///
/// Assignments only apply while Static is running - every other mode drives all
/// devices from one source, so <see cref="Enabled"/> gates them off.
/// </summary>
public sealed class StaticDeviceColorTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (byte R, byte G, byte B)> _colors = new(StringComparer.Ordinal);

    /// <summary>True while Static owns the output; false in every other mode.</summary>
    public bool Enabled { get; set; }

    public void Set(string id, byte r, byte g, byte b)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_lock) _colors[id] = (r, g, b);
    }

    public void Clear(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_lock) _colors.Remove(id);
    }

    public void ClearAll()
    {
        lock (_lock) _colors.Clear();
    }

    public bool TryGet(string id, out (byte R, byte G, byte B) color)
    {
        if (!Enabled || string.IsNullOrEmpty(id)) { color = default; return false; }
        lock (_lock) return _colors.TryGetValue(id, out color);
    }

    /// <summary>HSV with value pinned to full, matching the swatch the UI shows.</summary>
    public static (byte R, byte G, byte B) FromHueSaturation(float hue, float saturation)
    {
        var h = Math.Clamp(hue, 0f, 1f) * 6f;
        var s = Math.Clamp(saturation, 0f, 1f);
        var sector = (int)MathF.Floor(h) % 6;
        var f = h - MathF.Floor(h);
        var p = 1f - s;
        var q = 1f - s * f;
        var t = 1f - s * (1f - f);
        var (r, g, b) = sector switch
        {
            0 => (1f, t, p),
            1 => (q, 1f, p),
            2 => (p, 1f, t),
            3 => (p, q, 1f),
            4 => (t, p, 1f),
            _ => (1f, p, q),
        };
        return ((byte)MathF.Round(r * 255f), (byte)MathF.Round(g * 255f), (byte)MathF.Round(b * 255f));
    }
}
