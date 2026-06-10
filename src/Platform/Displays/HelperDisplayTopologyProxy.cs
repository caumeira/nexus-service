#if WINDOWS
using System.Collections.Generic;
using System.Runtime.Versioning;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Service-side IDisplayTopologyProvider that proxies to the user-session
/// helper: EnumDisplayMonitors/GetDpiForMonitor see nothing from Session 0,
/// same constraint as <see cref="HelperDisplayBrightnessProxy"/>. Returns
/// null (topology unknown) when no helper is connected.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HelperDisplayTopologyProxy : IDisplayTopologyProvider
{
    private readonly HelperRegistry _registry;

    public HelperDisplayTopologyProxy(HelperRegistry registry)
    {
        _registry = registry;
    }

    public bool PositionsAvailable => true;

    public IReadOnlyList<RawDisplayInfo>? Enumerate()
        => DisplayTopologyCommands.EnumerateAsync(_registry).GetAwaiter().GetResult();
}
#endif
