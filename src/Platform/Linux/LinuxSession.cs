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

    /// <summary>The session user's primary gid; pairs with <see cref="SessionUid"/>.</summary>
    public static uint? SessionGid { get; private set; }

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
        SessionGid = s.Gid;
        // Override unconditionally: this only runs as a root daemon with no
        // session env, where HOME/XDG_CONFIG_HOME are pre-set to root's by
        // sudo/systemd — set-if-unset would leave them pointing at /root.
        var run = $"/run/user/{s.Uid}";
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", run);
        Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", $"unix:path={run}/bus");
        if (!string.IsNullOrEmpty(s.Home))
        {
            // Adopt the user's home so config (settings.json, openrgb-config,
            // curves, profiles) and ~/.local browsers resolve to the user, not /root.
            Environment.SetEnvironmentVariable("HOME", s.Home);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(s.Home, ".config"));
        }
        Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", s.Wayland ?? "wayland-0");
        // Deliberately do NOT export DISPLAY/XAUTHORITY. The GPU lighting shader
        // context renders fully headless via EGL on the GPU device platform (see
        // LinuxEglContext) — no display required. Exporting DISPLAY would only
        // tempt a GLFW/GLX path that segfaults creating an nvidia GL context as
        // root on the user's XWayland (uncatchable native fault); EGL needs none
        // of it. Direct device control (identify, static) is unaffected either way.

        Console.Error.WriteLine($"[session] root daemon adopted session of uid {s.Uid} (home {s.Home}, wayland {s.Wayland})");
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

    private sealed record SessionInfo(uint Uid, uint Gid, string? Home, string? Display, string? Wayland);

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
            // Skip the display-manager greeter and other system users — adopting
            // gdm/sddm's session (uid < 1000) would point us at a bus the real
            // user can't use.
            if (uid < 1000)
                continue;
            var (gid, home) = PasswdForUid(uid);
            if (string.IsNullOrEmpty(home))
                continue; // no passwd entry — can't safely adopt this session
            var display = props.GetValueOrDefault("Display");
            return new SessionInfo(uid, gid, home, string.IsNullOrEmpty(display) ? null : display, WaylandSocket(uid));
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

    private static (uint Gid, string? Home) PasswdForUid(uint uid)
    {
        // getent passwd: name:x:uid:gid:gecos:home:shell
        var fields = ShellExecutor.Run("getent", "passwd", uid.ToString()).Trim().Split(':');
        var gid = fields.Length >= 4 && uint.TryParse(fields[3], out var g) ? g : uid;
        var home = fields.Length >= 6 && fields[5].Length > 0 ? fields[5] : null;
        return (gid, home);
    }

    /// <summary>
    /// Wrap a command so a root daemon spawns it as the session user (e.g. the
    /// dashboard browser): <c>setpriv</c> drops uid/gid + supplementary groups
    /// WITHOUT resetting env, so the child inherits our adopted
    /// XDG_RUNTIME_DIR/WAYLAND_DISPLAY and runs inside the user's compositor.
    /// Chromium refuses to run as root, and a root GUI client in a user's
    /// session is wrong anyway. Pass-through when not a root daemon.
    /// </summary>
    public static (string File, List<string> Args) WrapSpawnAsSessionUser(string file, List<string> args)
    {
        if (SessionUid is null || SessionGid is null)
            return (file, args);
        var wrapped = new List<string>
        {
            "--reuid", SessionUid.Value.ToString(),
            "--regid", SessionGid.Value.ToString(),
            "--init-groups", "--", file,
        };
        wrapped.AddRange(args);
        return ("setpriv", wrapped);
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
            finally
            {
                // A root daemon (ruid=suid=0) can always restore euid 0; if it
                // somehow can't, every later hardware write would fail forever —
                // crash instead so systemd restarts us clean.
                if (seteuid(0) != 0)
                    Environment.FailFast("[session] could not restore root euid after a session-bus connect");
            }
        }
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int seteuid(uint uid);
}
