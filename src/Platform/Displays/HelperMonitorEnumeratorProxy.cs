#if WINDOWS
using System.Collections.Generic;
using System.Runtime.Versioning;
using Qos.Service.Helper;
using Qos.Service.Helper.Domains;
using Qos.Service.Models.Lighting;
using Qos.Service.Platform;

namespace Qos.Service.Platform.Displays;

/// <summary>
/// Service-side IMonitorEnumerator that proxies to the user-session helper.
/// DXGI <c>EnumOutputs</c> returns nothing under Session 0 (LocalSystem
/// service), so the helper performs the enumeration in the interactive
/// session and the service relays the list via
/// <see cref="MonitorCommands.EnumerateAsync"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HelperMonitorEnumeratorProxy : IMonitorEnumerator
{
    private readonly HelperRegistry _registry;

    public HelperMonitorEnumeratorProxy(HelperRegistry registry)
    {
        _registry = registry;
    }

    public List<ScreenSyncMonitor> Enumerate()
        => MonitorCommands.EnumerateAsync(_registry).GetAwaiter().GetResult();
}
#endif
