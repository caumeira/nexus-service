#if WINDOWS
using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Helper;

namespace Qos.Service.Lifecycle;

/// <summary>
/// Entry point for <c>Qos.exe --helper</c> (and the legacy <c>--tray</c>
/// alias). Long-lived user-session companion process that owns the system
/// tray icon and acts as the named-pipe client to the LocalSystem service.
/// All Session 0-blind operations (foreground-window polling, SMTC media,
/// brightness control, etc.) run here and report to the service via the
/// pipe.
///
/// Single-instance per logon session via <c>Local\QosHelper</c>. Lifetime
/// is decoupled from <c>ShowWindowsTrayIcon</c> - that preference now
/// only controls icon visibility; the process keeps running so providers
/// it hosts stay alive.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class WindowsUserHelper
{
    private const int DefaultPort = 9400;
    private const string SessionMutexName = @"Local\QosHelper";

    private static readonly CancellationTokenSource s_exit = new();

    public static int Run(string[] args)
    {
        Diag("WindowsUserHelper.Run entered");
        if (!OperatingSystem.IsWindows()) return 0;

        using var mutex = new Mutex(initiallyOwned: true, name: SessionMutexName, out var isFirst);
        if (!isFirst)
        {
            Console.WriteLine("[helper] another helper is already running in this session; exiting");
            return 0;
        }

        // The helper outlives transient service stops. We only exit when the
        // service is uninstalled (no point staying), the session ends, or
        // we're explicitly told to stop. A stopped-but-installed service
        // means the pipe client will sit in its reconnect loop until SCM
        // brings the daemon back up.
        if (QueryServiceState() == ServiceState.NotInstalled)
        {
            Console.WriteLine("[helper] QosService not installed; helper exits");
            return 0;
        }

        // Tray icon: visible by default until the service tells us otherwise.
        // The server-driven model (HelperCommandClient.SetTrayVisibleAsync)
        // pushes the current ShowWindowsTrayIcon value on every successful
        // connect, so the icon settles to the persisted preference within
        // ~1s of bootstrap. Defaulting to "visible" avoids a transient
        // disappearance during service restarts.
        Platform.Windows.TrayIcon.Configure(
            DefaultPort,
            onExit: () => s_exit.Cancel());
        Platform.Windows.TrayIcon.SetVisible(true);

        // Watchdog: only exits the helper when the service is uninstalled.
        // Transient stopped states are fine; the pipe client handles them.
        var watchdog = new Thread(WatchdogLoop) { IsBackground = true };
        watchdog.Start();

        // Pipe client to the service. Reconnects with backoff on drop.
        // Outbound is shared with the helper-side providers (screen-time
        // poller, media pusher, etc.) so they can emit envelopes without
        // touching the pipe directly.
        var outbound = new HelperOutbound();

        // User-session providers. Each one owns its own polling/listening
        // and pushes envelopes through the shared outbound. Adding a new
        // domain = a new helper-side class + a service-side subscriber;
        // nothing else here changes.
        using var screenTime = new ScreenTimePoller(outbound);
        using var media = new MediaPusher(outbound);
        var brightness = new Platform.Displays.WindowsDisplayBrightnessProvider();

        var commands = new HelperClientCommands(
            setTrayVisible: visible => Platform.Windows.TrayIcon.SetVisible(visible),
            mediaControl: (source, action) => media.Control(source, action),
            getAlbumArt: source => media.GetAlbumArt(source),
            brightness: brightness);
        var client = new HelperClientLoop(commands, outbound);
        var pipeTask = Task.Run(() => client.RunAsync(s_exit.Token));

        try { s_exit.Token.WaitHandle.WaitOne(); }
        catch { }

        try { pipeTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        Platform.Windows.TrayIcon.SetVisible(false);
        return 0;
    }

    private static void WatchdogLoop()
    {
        while (!s_exit.IsCancellationRequested)
        {
            try { Thread.Sleep(5000); } catch { return; }
            if (s_exit.IsCancellationRequested) return;
            if (QueryServiceState() == ServiceState.NotInstalled)
            {
                Console.WriteLine("[helper] QosService uninstalled; helper exits");
                s_exit.Cancel();
                return;
            }
        }
    }

    private enum ServiceState { NotInstalled, Running, Other }

    private static ServiceState QueryServiceState()
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("query");
            psi.ArgumentList.Add(WindowsServiceInstaller.ServiceName);
            using var p = Process.Start(psi);
            if (p is null) return ServiceState.Other;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            // sc query returns 1060 / "service does not exist" when not installed.
            if (p.ExitCode != 0) return ServiceState.NotInstalled;
            return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)
                ? ServiceState.Running
                : ServiceState.Other;
        }
        catch { return ServiceState.Other; }
    }

    private static void Diag(string msg)
    {
        // File-based diagnostics: helper runs in the user session so
        // stdout/stderr go nowhere visible. Public dir is writable by
        // all user processes without needing prior dir setup.
        try
        {
            using var fs = new System.IO.FileStream(
                @"C:\Users\Public\qos-helper-debug.log",
                System.IO.FileMode.Append,
                System.IO.FileAccess.Write,
                System.IO.FileShare.ReadWrite);
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {msg}\n";
            var bytes = System.Text.Encoding.UTF8.GetBytes(line);
            fs.Write(bytes, 0, bytes.Length);
        }
        catch { }
    }
}
#endif
