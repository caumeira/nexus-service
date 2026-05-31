using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Nexus.Service.Platform;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Bridges a root system daemon to the active graphical login session.
///
/// Run as root (like coolercontrol's <c>coolercontrold</c>) Nexus gets full
/// hardware access — direct pwm writes, NVML fan control, kernel-module loading,
/// raw i2c/hidraw — but loses the per-user session context the tray (D-Bus
/// StatusNotifierItem), MPRIS media, <c>wpctl</c>/<c>pactl</c> volume, and the
/// dashboard launcher need. This detects the active seat's user via
/// <c>loginctl</c> and adopts their session environment (bus, runtime dir,
/// config home, display) so those features keep working, and so the daemon
/// reads the user's existing settings/profiles instead of root's empty home.
///
/// No-ops unless running as root with no session env already set — so a normal
/// <c>systemd --user</c> install or a dev run is untouched. Single active
/// graphical session is assumed (the desktop case); multi-seat picks the first.
/// </summary>
public static partial class LinuxSession
{
    /// <summary>
    /// The active session user's uid when running as a root daemon that adopted
    /// a session; null otherwise (normal --user run). Used to authenticate
    /// session-bus connects as that user (see <see cref="ConnectAsSessionUser"/>).
    /// </summary>
    public static uint? SessionUid { get; private set; }

    public static void AdoptActiveSessionEnv()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // A --user service / dev run already has the session env; only a root
        // daemon arrives here bare.
        if (!IsRoot() || HasSessionEnv())
            return;

        var s = Detect();
        if (s is null)
        {
            Console.Error.WriteLine("[session] root daemon: no active graphical session found; session features (tray/media/volume) disabled this run");
            return;
        }

        SessionUid = s.Uid;
        var run = $"/run/user/{s.Uid}";
        SetIfUnset("XDG_RUNTIME_DIR", run);
        SetIfUnset("DBUS_SESSION_BUS_ADDRESS", $"unix:path={run}/bus");
        if (!string.IsNullOrEmpty(s.Home))
        {
            // Adopt the user's home so config (settings.json, openrgb-config,
            // curves, profiles) resolves to their existing ~/.config, not /root.
            SetIfUnset("HOME", s.Home);
            SetIfUnset("XDG_CONFIG_HOME", Path.Combine(s.Home, ".config"));
            SetIfUnset("XAUTHORITY", Path.Combine(s.Home, ".Xauthority"));
        }
        SetIfUnset("WAYLAND_DISPLAY", s.Wayland ?? "wayland-0");
        if (!string.IsNullOrEmpty(s.Display))
            SetIfUnset("DISPLAY", s.Display);

        Console.Error.WriteLine($"[session] root daemon adopted session of uid {s.Uid} (home {s.Home}, display {s.Display ?? s.Wayland})");
    }

    private static bool IsRoot()
    {
        // Environment.UserName resolves via getpwuid(geteuid()) on Unix, so it
        // reflects the effective user — "root" for a root daemon.
        try { return string.Equals(Environment.UserName, "root", StringComparison.Ordinal); }
        catch { return false; }
    }

    private static bool HasSessionEnv()
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"));

    private sealed record SessionInfo(uint Uid, string? Home, string? Display, string? Wayland);

    private static SessionInfo? Detect()
    {
        // First session id on each line of `loginctl list-sessions --no-legend`.
        var list = ShellExecutor.Run("loginctl", "list-sessions", "--no-legend");
        foreach (var raw in list.Split('\n'))
        {
            var id = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrEmpty(id))
                continue;
            var props = ParseProps(ShellExecutor.Run(
                "loginctl", "show-session", id, "-p", "Active", "-p", "Type", "-p", "User", "-p", "Display"));
            if (!string.Equals(props.GetValueOrDefault("Active"), "yes", StringComparison.OrdinalIgnoreCase))
                continue;
            var type = props.GetValueOrDefault("Type");
            if (type != "wayland" && type != "x11")
                continue; // skip ttys / non-graphical sessions
            if (!uint.TryParse(props.GetValueOrDefault("User"), out var uid))
                continue;
            var display = props.GetValueOrDefault("Display");
            return new SessionInfo(uid, HomeForUid(uid), string.IsNullOrEmpty(display) ? null : display, WaylandSocket(uid));
        }
        return null;
    }

    // `key=value` lines from loginctl show-session.
    private static Dictionary<string, string> ParseProps(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var eq = line.IndexOf('=');
            if (eq > 0)
                d[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return d;
    }

    private static string? HomeForUid(uint uid)
    {
        // getent passwd: name:x:uid:gid:gecos:home:shell
        var pw = ShellExecutor.Run("getent", "passwd", uid.ToString());
        var fields = pw.Trim().Split(':');
        return fields.Length >= 6 && fields[5].Length > 0 ? fields[5] : null;
    }

    private static string? WaylandSocket(uint uid)
    {
        try
        {
            var sock = Directory.GetFiles($"/run/user/{uid}", "wayland-*")
                .FirstOrDefault(f => !f.EndsWith(".lock", StringComparison.Ordinal));
            return sock is null ? null : Path.GetFileName(sock);
        }
        catch { return null; }
    }

    private static void SetIfUnset(string name, string value)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            Environment.SetEnvironmentVariable(name, value);
    }

    private static readonly object EuidGate = new();

    /// <summary>
    /// Run <paramref name="connect"/> with the effective uid temporarily dropped
    /// to the session user, then restore root. A D-Bus session bus authenticates
    /// by the peer's <c>SO_PEERCRED</c> (effective uid at connect time) and
    /// rejects root, so the socket connect must happen as the user; the resulting
    /// connection keeps that identity after euid is restored. No-op (just calls
    /// <paramref name="connect"/>) when not a root daemon. Serialized so two
    /// connects can't race the process-wide euid.
    /// </summary>
    public static void ConnectAsSessionUser(Action connect)
    {
        var uid = SessionUid;
        if (uid is null || !OperatingSystem.IsLinux())
        {
            connect();
            return;
        }
        lock (EuidGate)
        {
            if (seteuid(uid.Value) != 0)
            {
                connect(); // couldn't drop — try anyway (will likely fail upstream)
                return;
            }
            try { connect(); }
            finally { seteuid(0); }
        }
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int seteuid(uint uid);
}
