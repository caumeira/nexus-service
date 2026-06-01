using System;
using Nexus.Service.Platform;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Linux "start at login" toggle. Nexus runs as a <b>root systemd system
/// unit</b> (<c>nexus.service</c>, installed by <c>installer/linux/install.sh</c>),
/// so the truthful boot toggle is enabling/disabling that unit — NOT an XDG
/// autostart <c>.desktop</c>. The old implementation wrote
/// <c>~/.config/autostart/nexus.desktop</c> with <c>Exec=/opt/nexus/Nexus</c>,
/// which at next login launched a <em>second, non-root copy of the whole
/// service</em> in the user session (port-bind clash / config written to the
/// wrong owner). The daemon already runs as root, so it can toggle its own unit
/// via <c>systemctl</c> with no sudo. The <c>path</c>/<c>arguments</c> args are
/// ignored: the unit file already encodes <c>ExecStart</c>.
///
/// In a dev <c>--user</c> run there is no installed system unit, so
/// <c>systemctl</c> fails and the toggle reports false (best-effort, logged).
/// AOT-safe (Process.Start via <see cref="ShellExecutor"/>).
/// </summary>
public sealed class LinuxStartupProvider : IStartupProvider
{
    internal const string UnitName = "nexus.service";

    // Seams so tests don't shell out to a real systemd. Default impls call
    // systemctl: _run for enable/disable (exit code), _query for is-enabled.
    private readonly Func<string[], int> _run;
    private readonly Func<string[], string> _query;

    public LinuxStartupProvider() : this(
        args => ShellExecutor.RunExit("systemctl", ShellExecutor.DefaultTimeoutMs, args),
        args => ShellExecutor.Run("systemctl", args))
    { }

    internal LinuxStartupProvider(Func<string[], int> run, Func<string[], string> query)
    {
        _run = run;
        _query = query;
    }

    public bool IsEnabled()
    {
        // `systemctl is-enabled nexus.service` prints "enabled" when it starts at
        // boot; "disabled"/"masked"/"static"/... or empty (no unit) otherwise.
        var status = _query(new[] { "is-enabled", UnitName }).Trim();
        return status is "enabled" or "enabled-runtime";
    }

    public bool SetEnabled(bool enabled, string path, string arguments)
    {
        var verb = enabled ? "enable" : "disable";
        var exit = _run(new[] { verb, UnitName });
        if (exit != 0)
        {
            Console.Error.WriteLine(
                $"[linux-startup] systemctl {verb} {UnitName} failed (exit {exit}) — " +
                "not root, or the system unit isn't installed (dev --user run?).");
        }
        return exit == 0;
    }
}
