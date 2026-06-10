using System.Collections.Generic;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Platform source of OS monitor topology (positions, modes, scale, identity).
/// Distinct from <see cref="IDisplayBrightnessProvider"/> (control-focused) and
/// <see cref="Nexus.Service.Platform.IMonitorEnumerator"/> (screen-sync capture
/// indexes); ids here must match the brightness provider's id space so panel
/// assignments and DDC controls join on the same key.
/// </summary>
public interface IDisplayTopologyProvider
{
    /// <summary>
    /// True when monitor positions are meaningful on this platform. Linux DRM
    /// sysfs exposes connectors but not compositor layout.
    /// </summary>
    bool PositionsAvailable { get; }

    /// <summary>
    /// Enumerate attached monitors. Null = topology unavailable right now
    /// (e.g. the user-session helper is not connected), as opposed to an
    /// empty list meaning "no monitors".
    /// </summary>
    IReadOnlyList<RawDisplayInfo>? Enumerate();
}
