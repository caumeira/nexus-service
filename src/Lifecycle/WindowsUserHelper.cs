#if WINDOWS
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Entry point for <c>Nexus.exe --helper</c> (and the legacy <c>--tray</c>
/// alias). Long-lived user-session companion process that owns the system
/// tray icon and acts as the named-pipe client to the LocalSystem service.
/// All Session 0-blind operations (foreground-window polling, SMTC media,
/// brightness control, etc.) run here and report to the service via the
/// pipe.
///
/// Single-instance per logon session via <c>Local\NexusHelper</c>. Lifetime
/// is decoupled from <c>ShowWindowsTrayIcon</c> (which controls icon
/// visibility only); the process keeps running so the providers it hosts
/// stay alive.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class WindowsUserHelper
{
    private const int DefaultPort = 9400;
    private const string SessionMutexName = @"Local\NexusHelper";

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
            Console.WriteLine("[helper] NexusService not installed; helper exits");
            return 0;
        }

        // Pipe client to the service. Reconnects with backoff on drop.
        // Outbound is shared with the helper-side providers (screen-time
        // poller, media pusher, etc.) so they can emit envelopes without
        // touching the pipe directly. Constructed before the tray so the
        // "Shut down" closure can capture it for the stop-request send.
        var outbound = new HelperOutbound();

        // Tray icon: visible by default until the service tells us otherwise.
        // The server-driven model (HelperCommandClient.SetTrayVisibleAsync)
        // pushes the current ShowWindowsTrayIcon value on every successful
        // connect, so the icon settles to the persisted preference within
        // ~1s of bootstrap. Defaulting to "visible" avoids a transient
        // disappearance during service restarts.
        //
        // "Shut down" in the tray menu must match the settings "Stop Nexus"
        // UX: service stopped, --app window closed, tray gone. The stop
        // request rides the existing pipe (already authenticated) so the
        // service runs its graceful StopApplication path; sc.exe stop
        // would 5 (Access Denied) here because the SCM DACL only grants
        // Authenticated Users START + QUERY, not STOP.
        Platform.Windows.TrayIcon.Configure(
            DefaultPort,
            onExit: () =>
            {
                try { Platform.Windows.TrayIcon.CloseAppWindow(); } catch { }
                try { RequestServiceStop(outbound); } catch { }
                s_exit.Cancel();
            });
        Platform.Windows.TrayIcon.SetVisible(true);

        // Watchdog: only exits the helper when the service is uninstalled.
        // Transient stopped states are fine; the pipe client handles them.
        var watchdog = new Thread(WatchdogLoop) { IsBackground = true };
        watchdog.Start();

        // User-session providers. Each one owns its own polling/listening
        // and pushes envelopes through the shared outbound. Adding a new
        // domain = a new helper-side class + a service-side subscriber;
        // nothing else here changes.
        using var screenTime = new ScreenTimePoller(outbound);
        using var media = new MediaPusher(outbound);
        using var screenCapture = new ScreenCapturePusher(outbound);
        var brightness = new Platform.Displays.WindowsDisplayBrightnessProvider();

        // Each domain registers its own envelope handler against this
        // registry. Adding a new domain = create the handler class in
        // Helper/Domains/ and call .Register(handlerRegistry) here.
        var handlerRegistry = new HelperHandlerRegistry();
        new TrayHandler(
            Platform.Windows.TrayIcon.SetVisible,
            Platform.Windows.TrayIcon.ShowPairBalloon,
            Platform.Windows.TrayIcon.ClearPairBalloon).Register(handlerRegistry);
        new LifecycleHandler(
            onShutdown: () =>
            {
                // Service-driven teardown: it's stopping (e.g. user hit
                // "Stop Nexus" in settings), so close the --app window and
                // exit the helper so the tray icon goes too. We do NOT
                // also send service.requestStop here - that would echo
                // the very stop the service has already initiated. Tray
                // onExit is the symmetric path that pushes the stop the
                // other way; do not "fix" this asymmetry.
                try { Platform.Windows.TrayIcon.CloseAppWindow(); } catch { }
                s_exit.Cancel();
            },
            onOverlayPrefsChanged: () =>
            {
                // Wake the overlay's marshaler so it repolls preferences
                // immediately (e.g. the user toggled widgets off). We run
                // in the same Windows session as nexus-overlay, so FindWindow
                // can see the marshaler that the service-side cannot.
                try
                {
                    var hwnd = FindWindowW(OverlayMarshalerClassName, null);
                    if (hwnd != IntPtr.Zero)
                    {
                        var msg = RegisterWindowMessageW(OverlayPrefsChangedMessageName);
                        if (msg != 0) PostMessageW(hwnd, msg, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                catch { /* best-effort wake; 5 s poll is the safety net */ }
            }).Register(handlerRegistry);
        new MediaHandler(media.Control, media.GetAlbumArt).Register(handlerRegistry);
        new BrightnessHandler(brightness).Register(handlerRegistry);
        new ShortcutsHandler(new Nexus.Service.Activity.WindowsShortcutsProvider()).Register(handlerRegistry);
        new FileDialogHandler().Register(handlerRegistry);
        new MonitorsHandler().Register(handlerRegistry);
        new DisplaysHandler().Register(handlerRegistry);
        // WM_DISPLAYCHANGE lands on the tray's hidden top-level window; push
        // it to the service so dashboards refetch /displays/topology. Fired
        // on the message-pump thread, so the send is fire-and-forget.
        Platform.Windows.TrayIcon.DisplayChanged += () =>
        {
            _ = outbound.SendAsync(
                type: DisplayTopologyCommands.ChangedType,
                payload: new DisplaysChangedPayload(),
                payloadType: Serialization.AppJsonContext.Default.DisplaysChangedPayload);
        };
        new OrientationHandler(new Platform.Displays.WindowsDisplayOrientationProvider()).Register(handlerRegistry);
        new ScreenMirrorHandler(screenCapture.Start, screenCapture.Stop).Register(handlerRegistry);
        // Foregrounded variant of LogsFolder.Open: the helper is a background
        // process, so a plain explorer spawn lands behind the app window.
        new DiagnosticsHandler(() => Platform.Windows.ForegroundNudge.OpenFolderOverApp(
            Nexus.Service.Platform.ServiceLog.LogsDirectory)).Register(handlerRegistry);

        var client = new HelperClientLoop(handlerRegistry, outbound);
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
                Console.WriteLine("[helper] NexusService uninstalled; helper exits");
                s_exit.Cancel();
                return;
            }
        }
    }

    /// <summary>
    /// Ask the service to stop, over the pipe. The service handler calls
    /// IHostApplicationLifetime.StopApplication so the daemon runs the
    /// same graceful path /service/stop uses. We briefly block to make
    /// sure the frame actually leaves the wire before the caller cancels
    /// s_exit (which tears down the pipe loop); a timeout falls through
    /// if the service isn't currently connected, in which case there's
    /// no daemon to stop anyway.
    /// </summary>
    private static void RequestServiceStop(HelperOutbound outbound)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            outbound.SendAsync(
                type: "service.requestStop",
                payload: new ServiceRequestStopPayload(),
                payloadType: Nexus.Service.Serialization.AppJsonContext.Default.ServiceRequestStopPayload,
                ct: cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[helper] pipe stop request failed: {ex.Message}");
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

    // Win32 plumbing for the cross-process overlay marshaler wake. Lives
    // here rather than in a shared file because this is the only consumer.
    private const string OverlayMarshalerClassName = "Nexus.Overlay.Marshaler";
    private const string OverlayPrefsChangedMessageName = "Nexus.Overlay.PrefsChanged";

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private static void Diag(string msg)
    {
        // File-based diagnostics: helper runs in the user session so
        // stdout/stderr go nowhere visible. Public dir is writable by
        // all user processes without needing prior dir setup.
        try
        {
            using var fs = new System.IO.FileStream(
                @"C:\Users\Public\nexus-helper-debug.log",
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
