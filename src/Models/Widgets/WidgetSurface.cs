using System;

namespace Qos.Service.Models.Widgets;

/// <summary>
/// Canonical surface names. Bundle manifests list strings from this set in
/// the <c>surfaces</c> array; the host filters listings + adverts by them.
/// Phase 0 ships only the dashboard surface; <c>Panel</c> and <c>Overlay</c>
/// are reserved for Phase 8.
/// </summary>
public static class WidgetSurface
{
    public const string Dashboard = "dashboard";
    public const string Panel = "panel";
    public const string Overlay = "overlay";

    public static bool IsKnown(string value) =>
        value is Dashboard or Panel or Overlay;

    public static bool IsActiveInPhase0(string value) =>
        string.Equals(value, Dashboard, StringComparison.Ordinal);
}
