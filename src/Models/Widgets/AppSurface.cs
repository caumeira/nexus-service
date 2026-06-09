namespace Nexus.Service.Models.Widgets;

/// <summary>
/// Canonical surface names accepted in a widget manifest's <c>surfaces</c>
/// array.
/// </summary>
public static class AppSurface
{
    public const string Dashboard = "dashboard";
    public const string Panel = "panel";
    public const string Overlay = "overlay";

    public static bool IsKnown(string value) =>
        value is Dashboard or Panel or Overlay;
}
