using System;
using System.Collections.Generic;
using Nexus.Service.Platform;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Desktop notifications for the Linux tray via notify-send (libnotify), run as
/// the active session user. The root daemon can't reach the user's notification
/// daemon directly (it lives on the user's session bus), so the command is
/// wrapped with <see cref="LinuxSession.WrapSpawnAsSessionUser"/>: the same
/// session-user spawn path the browser and screencast helper launches take.
///
/// Chosen over hand-marshalling org.freedesktop.Notifications.Notify (signature
/// susssasa-sv-i) on the shared D-Bus client: notify-send is universally
/// present, runs on the correct (user) bus, and the argument build is
/// unit-testable without a live bus. Best-effort: a missing libnotify just logs
/// a nonzero exit, it never throws into the caller.
/// </summary>
internal static class LinuxNotify
{
    /// <summary>
    /// Build the (file, args) to display a desktop notification as the session
    /// user. On a non-root dev --user run (or non-Linux) WrapSpawnAsSessionUser
    /// returns the command unwrapped.
    /// </summary>
    internal static (string File, List<string> Args) BuildCommand(string summary, string body, string icon = "nexus")
    {
        var args = new List<string> { "-a", "Nexus", "-i", icon, summary, body };
        return LinuxSession.WrapSpawnAsSessionUser("notify-send", args);
    }

    /// <summary>Fire-and-verify a desktop notification; swallows + logs failures.</summary>
    internal static void Send(string summary, string body)
    {
        if (!OperatingSystem.IsLinux())
            return;
        try
        {
            var (file, args) = BuildCommand(summary, body);
            var exit = ShellExecutor.RunExit(file, 4000, args.ToArray());
            if (exit != 0)
                Console.Error.WriteLine($"[tray] notify-send exited {exit} (libnotify / notification daemon missing?)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tray] notify-send failed: {ex.Message}");
        }
    }
}
