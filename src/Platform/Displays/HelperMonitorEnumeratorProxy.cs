#if WINDOWS
using System.Collections.Generic;
using System.Runtime.Versioning;
using Qos.Service.Helper;
using Qos.Service.Models.Lighting;
using Qos.Service.Platform;

namespace Qos.Service.Platform.Displays;

/// <summary>
/// Service-side IMonitorEnumerator that proxies to the user-session helper.
/// DXGI EnumOutputs returns nothing under Session 0 (LocalSystem service),
/// so the helper performs the enumeration in the interactive session and
/// the service relays the list. Blocks on the pipe RPC - the call site
/// (GET /lighting/screen/monitors) is async-ready and the RPC has a short
/// timeout in <see cref="HelperCommandClient"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HelperMonitorEnumeratorProxy : IMonitorEnumerator
{
    private readonly HelperCommandClient _commands;

    public HelperMonitorEnumeratorProxy(HelperCommandClient commands)
    {
        _commands = commands;
    }

    public List<ScreenSyncMonitor> Enumerate()
        => _commands.MonitorEnumerateAsync().GetAwaiter().GetResult();
}
#endif
