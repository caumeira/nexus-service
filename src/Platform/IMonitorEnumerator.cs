using System.Collections.Generic;
using Nexus.Service.Models.Lighting;

namespace Nexus.Service.Platform;

/// <summary>
/// Source of attached-display info for the screen-mirror UI. Two impls:
/// <see cref="DefaultMonitorEnumerator"/> calls <see cref="MonitorEnumerator.List"/>
/// directly (works on macOS / when the service hosts a user session itself);
/// <see cref="Displays.HelperMonitorEnumeratorProxy"/> proxies to the
/// user-session helper, which is required when the service runs as
/// LocalSystem (Session 0) where DXGI returns no outputs.
/// </summary>
public interface IMonitorEnumerator
{
    List<ScreenSyncMonitor> Enumerate();
}
