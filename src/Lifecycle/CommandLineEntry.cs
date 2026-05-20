namespace Qos.Service.Lifecycle;

// Early-exit CLI dispatch + flag parsing for Program.cs. Each handler
// short-circuits the daemon startup; the rest of the binary never builds
// the WebApplication. Kept here so Program.cs reads as the run-the-daemon
// path, not a switch on argv[0].
internal static class CommandLineEntry
{
#if WINDOWS
    private static readonly Dictionary<string, Func<string[], int>> WindowsHandlers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["--install"] = WindowsServiceInstaller.RunInstall,
            ["--uninstall"] = WindowsServiceInstaller.RunUninstall,
            ["--start-service"] = static _ => WindowsServiceInstaller.RunStartService(),
            // --tray is the legacy alias kept for existing HKCU\Run entries on
            // pre-helper installs. Both route to the same user-session companion.
            ["--helper"] = WindowsUserHelper.Run,
            ["--tray"] = WindowsUserHelper.Run,
            // One-shot invoked by the service via schtasks when the desktop
            // widget context menu's "Open dashboard" item is clicked.
            ["--open-app"] = static _ =>
            {
                Qos.Service.Platform.Windows.TrayIcon.OpenLocalWindow();
                return 0;
            },
        };
#endif

    // Returns the process exit code if argv matched a short-circuit path,
    // otherwise null. Must run before the single-instance mutex because the
    // PawnIO install is briefly a second instance during the elevated install.
    public static int? TryEarlyExit(string[] args)
    {
        if (args.Length > 0 && args[0] == "--install-pawnio")
            return PawnIoInstaller.RunElevatedInstall();

#if WINDOWS
        if (args.Length > 0 && WindowsHandlers.TryGetValue(args[0], out var handler))
            return handler(args);

        // No-args means the user double-clicked Qos.exe. With SCM owning the
        // daemon, the launcher just detects service state, spawns the tray if
        // missing, and opens the dashboard - no cold-start self-elevation.
        if (args.Length == 0)
            return WindowsLauncher.Run();

        // Protocol-handler URLs from the dashboard. start-admin is kept as a
        // legacy alias mapping onto restart-service; the service is already
        // LocalSystem so "restart as admin" is a no-op naming-wise.
        if (args.Length > 0 && (
                args[0].StartsWith("qos://restart-service", StringComparison.OrdinalIgnoreCase)
                || args[0].StartsWith("qos://start-admin", StringComparison.OrdinalIgnoreCase)))
            return WindowsServiceInstaller.RunStartService();
#endif
        return null;
    }

    // Strips the lifecycle flags Program.cs uses to gate behaviour from the
    // forwarded args, returning them as named booleans. The flag-removal order
    // mirrors the original sequence so --relaunch-elevated is checked against
    // args[0] only after --service has been pulled out.
    public static (string[] Args, bool ServiceMode, bool SuppressStartupWindow, bool RelaunchElevated)
        StripLifecycleFlags(string[] args)
    {
        var serviceMode = args.Any(static a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase));
        if (serviceMode)
            args = args.Where(static a => !string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase)).ToArray();

        var relaunchElevated = args.Length > 0 && args[0] == "--relaunch-elevated";
        if (relaunchElevated) args = args.Skip(1).ToArray();

        var suppressStartupWindow = args.Any(static a => string.Equals(a, "--no-window", StringComparison.OrdinalIgnoreCase));
        if (suppressStartupWindow)
            args = args.Where(static a => !string.Equals(a, "--no-window", StringComparison.OrdinalIgnoreCase)).ToArray();

        return (args, serviceMode, suppressStartupWindow, relaunchElevated);
    }
}
