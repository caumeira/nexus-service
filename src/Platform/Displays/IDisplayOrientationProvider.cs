namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Rotates the physical Y70 panel display via the Windows display subsystem.
/// On the service side this is fronted by <c>HelperDisplayOrientationProxy</c>,
/// which routes the call through the user-session helper because Session 0
/// cannot see the user's monitors.
/// </summary>
public interface IDisplayOrientationProvider
{
    /// <summary>
    /// Apply the requested orientation to the Y70 display, if connected.
    /// </summary>
    /// <param name="orientation">"Landscape", "Portrait", "LandscapeFlipped", or "PortraitFlipped".</param>
    /// <returns>(ok, error). <c>error</c> is empty on success or when no Y70 is attached.</returns>
    (bool Ok, string Error) SetY70Orientation(string orientation);
}

public sealed class NoopDisplayOrientationProvider : IDisplayOrientationProvider
{
    public (bool Ok, string Error) SetY70Orientation(string orientation) => (true, "");
}
