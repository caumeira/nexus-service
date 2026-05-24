#if WINDOWS
using System.Collections.Generic;
using System.Runtime.Versioning;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Service-side IDisplayBrightnessProvider that proxies every call to the
/// user-session helper. The DDC/CI and laptop-panel APIs the brightness
/// provider uses don't work reliably from Session 0; the helper runs the
/// real <see cref="WindowsDisplayBrightnessProvider"/> in the user session
/// and we route through <see cref="BrightnessCommands"/>.
///
/// All methods block on the pipe RPC. The IDisplayBrightnessProvider
/// interface is synchronous and the call sites (HTTP route handlers) are
/// async-ready, so a short blocking await is acceptable.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HelperDisplayBrightnessProxy : IDisplayBrightnessProvider
{
    private readonly HelperRegistry _registry;

    public HelperDisplayBrightnessProxy(HelperRegistry registry)
    {
        _registry = registry;
    }

    public string Hint => BrightnessCommands.HintAsync(_registry).GetAwaiter().GetResult();

    public IReadOnlyList<DisplayDto> Enumerate()
        => BrightnessCommands.EnumerateAsync(_registry).GetAwaiter().GetResult();

    public int? GetBrightness(string id)
        => BrightnessCommands.GetAsync(_registry, id).GetAwaiter().GetResult();

    public DisplayBrightnessDto SetBrightness(string id, int percent)
        => BrightnessCommands.SetAsync(_registry, id, percent).GetAwaiter().GetResult();

    public DisplayBrightnessWritePolicy GetBrightnessWritePolicy(string id)
        => BrightnessCommands.PolicyAsync(_registry, id).GetAwaiter().GetResult();

    public DisplayVcpDto? GetVcp(string id, byte code)
        => BrightnessCommands.GetVcpAsync(_registry, id, code).GetAwaiter().GetResult();

    public bool SetVcp(string id, byte code, int value)
        => BrightnessCommands.SetVcpAsync(_registry, id, code, value).GetAwaiter().GetResult();
}
#endif
