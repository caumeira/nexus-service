using System;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Physical facts for a display model the promoted-monitor grid math knows
/// (<see cref="Dpi"/> is native px/inch, not the Windows scaling DPI).
/// </summary>
public sealed record KnownPanelDisplay(string Family, double Dpi);

/// <summary>
/// Curated displays commonly promoted to Nexus panels, matched by the EDID
/// product name the OS reports. Supplies the physical density the grid needs
/// (Windows only exposes scaling DPI) and a family id for web-side branding.
/// </summary>
public static class KnownPanelDisplays
{
    public const string XeneonEdgeFamily = "xeneon-edge";

    // Corsair Xeneon Edge: 14.5" 2560x720 (32:9) touch strip.
    // sqrt(2560^2 + 720^2) / 14.5 = 183 px/inch.
    private static readonly KnownPanelDisplay XeneonEdge = new(XeneonEdgeFamily, 183);

    public static KnownPanelDisplay? Match(string? name, string? model)
    {
        if (ContainsIgnoreCase(name, "XENEON EDGE") || ContainsIgnoreCase(model, "XENEON EDGE"))
            return XeneonEdge;
        return null;
    }

    private static bool ContainsIgnoreCase(string? value, string fragment) =>
        !string.IsNullOrEmpty(value) && value.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
