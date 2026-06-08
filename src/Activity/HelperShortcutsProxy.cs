#if WINDOWS
using System.Collections.Generic;
using System.Runtime.Versioning;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

/// <summary>
/// Service-side IShortcutsProvider that enumerates installed apps + icons through
/// the user-session helper (Get-StartApps is per-user and empty from Session 0).
/// Launch stays direct — explorer.exe delegates to the user's shell.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HelperShortcutsProxy : IShortcutsProvider
{
    private readonly HelperRegistry _registry;
    private readonly WindowsShortcutsProvider _direct = new();

    public HelperShortcutsProxy(HelperRegistry registry) { _registry = registry; }

    public IReadOnlyList<Shortcut> GetAll()
        => ShortcutsCommands.GetAllAsync(_registry).GetAwaiter().GetResult();

    public Shortcut? GetById(string targetId)
        => ShortcutsCommands.GetByIdAsync(_registry, targetId).GetAwaiter().GetResult();

    public byte[] GetIcon(string targetId)
        => ShortcutsCommands.GetIconAsync(_registry, targetId).GetAwaiter().GetResult();

    public bool Launch(string targetId) => _direct.Launch(targetId);
}
#endif
