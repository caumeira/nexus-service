#if WINDOWS
using System.Collections.Generic;
using System.Runtime.Versioning;
using Qos.Service.Helper;
using Qos.Service.Models.Displays;

namespace Qos.Service.Platform.Displays;

/// <summary>
/// Service-side IDisplayBrightnessProvider that proxies every call to the
/// user-session helper. The DDC/CI and laptop-panel APIs the brightness
/// provider uses don't work reliably from Session 0; the helper runs the
/// real <see cref="WindowsDisplayBrightnessProvider"/> in the user session
/// and we route through it.
///
/// All methods block on the pipe RPC. The IDisplayBrightnessProvider
/// interface is synchronous and the call sites (HTTP route handlers) are
/// async-ready, so a short blocking await is acceptable.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HelperDisplayBrightnessProxy : IDisplayBrightnessProvider
{
    private readonly HelperCommandClient _commands;

    public HelperDisplayBrightnessProxy(HelperCommandClient commands)
    {
        _commands = commands;
    }

    public string Hint => _commands.BrightnessHintAsync().GetAwaiter().GetResult();

    public IReadOnlyList<DisplayDto> Enumerate()
        => _commands.BrightnessEnumerateAsync().GetAwaiter().GetResult();

    public int? GetBrightness(string id)
        => _commands.BrightnessGetAsync(id).GetAwaiter().GetResult();

    public DisplayBrightnessDto SetBrightness(string id, int percent)
        => _commands.BrightnessSetAsync(id, percent).GetAwaiter().GetResult();

    public DisplayBrightnessWritePolicy GetBrightnessWritePolicy(string id)
        => _commands.BrightnessPolicyAsync(id).GetAwaiter().GetResult();

    public DisplayVcpDto? GetVcp(string id, byte code)
        => _commands.BrightnessGetVcpAsync(id, code).GetAwaiter().GetResult();

    public bool SetVcp(string id, byte code, int value)
        => _commands.BrightnessSetVcpAsync(id, code, value).GetAwaiter().GetResult();
}
#endif
