namespace Nexus.Service.Models.Panel;

/// <summary>
/// Canonical <c>PanelDeviceCapabilities.Surface</c> values. The SPA infers
/// its surface from the viewport for self-registered devices; service-created
/// records (monitor promotion) stamp it explicitly.
/// </summary>
public static class PanelSurfaces
{
    public const string Y70 = "y70";
    public const string Q60 = "q60";
    public const string Phone = "phone";
    public const string Desktop = "desktop";
    /// <summary>A user-promoted OS monitor hosting a fullscreen panel kiosk.</summary>
    public const string Monitor = "monitor";
}
