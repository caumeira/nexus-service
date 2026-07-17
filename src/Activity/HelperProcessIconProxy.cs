#if WINDOWS
using System.Runtime.Versioning;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;

namespace Nexus.Service.Activity;

/// <summary>Service-side IProcessIconProvider that runs extraction through
/// the user-session helper (WindowsIconExtractor only ever runs there - see
/// HelperShortcutsProxy for the same constraint on shortcut icons).</summary>
[SupportedOSPlatform("windows")]
public sealed class HelperProcessIconProxy : IProcessIconProvider
{
    private readonly HelperRegistry _registry;

    public HelperProcessIconProxy(HelperRegistry registry) { _registry = registry; }

    public byte[] GetIcon(string exePath)
        => ProcessIconCommands.ExtractAsync(_registry, exePath).GetAwaiter().GetResult();
}
#endif
