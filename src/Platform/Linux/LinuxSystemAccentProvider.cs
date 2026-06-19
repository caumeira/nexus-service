using System;
using System.Collections.Generic;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// OS accent from the XDG desktop portal (<c>org.freedesktop.appearance</c>
/// <c>accent-color</c>) - the desktop-agnostic source. KDE leaves no AccentColor
/// in kdeglobals when it derives from the active scheme, but the portal returns
/// it as a <c>(ddd)</c> RGB tuple in 0..1 regardless. Read via gdbus on the
/// user's session bus, wrapped for the root daemon the same way LinuxNotify is.
/// On demand only - the portal also emits a change signal, but the accent
/// changes rarely and the web refetches on load / source change.
/// </summary>
public sealed class LinuxSystemAccentProvider : ISystemAccentProvider
{
    public string? GetAccentHex()
    {
        if (!OperatingSystem.IsLinux())
            return null;
        try
        {
            var (file, args) = LinuxSession.WrapSpawnAsSessionUser("gdbus", new List<string>
            {
                "call", "--session",
                "--dest", "org.freedesktop.portal.Desktop",
                "--object-path", "/org/freedesktop/portal/desktop",
                "--method", "org.freedesktop.portal.Settings.ReadOne",
                "org.freedesktop.appearance", "accent-color",
            });
            return PortalAccent.Parse(ShellExecutor.Run(file, 3000, args.ToArray()));
        }
        catch
        {
            return null;
        }
    }
}
