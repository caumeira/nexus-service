#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

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
    private const uint WM_CLOSE = 0x0010;
    private const string UpdaterWindowTitle = "Nexus Updater";
    private static readonly string ReopenFlagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Nexus",
        "reopen-dashboard.flag");

    private static readonly CancellationTokenSource s_exit = new();

    // Immutable snapshot of the latest profiles.list push. Written by the pipe
    // handler thread; read on the tray message-pump thread. Volatile reference
    // swap to an immutable object is the safe cross-thread publish pattern.
    private sealed class ProfileSnapshot
    {
        public IReadOnlyList<(string Id, string Name)> Items { get; }
        public string ActiveId { get; }

        public ProfileSnapshot(IReadOnlyList<(string Id, string Name)> items, string activeId)
        {
            Items = items;
            ActiveId = activeId;
        }
    }

    private static volatile ProfileSnapshot s_profiles = new(Array.Empty<(string, string)>(), "");

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

        // If a reopen flag was written before the installer launched, open
        // the dashboard now that the helper owns the tray, and close any
        // updater window that mshta may still be showing.
        if (File.Exists(ReopenFlagPath))
        {
            _ = Task.Run(() =>
            {
                // Open the dashboard FIRST: it is the goal and must not be
                // blocked by the flag delete, which throws when the helper
                // (user session) cannot remove the LocalSystem-written flag.
                try { Platform.Windows.TrayIcon.OpenLocalWindow(); } catch { /* best-effort */ }
                try { CloseUpdaterWindow(); } catch { /* best-effort */ }
                try { File.Delete(ReopenFlagPath); } catch { /* service grants the user delete; ignore if it fails */ }
            });
        }

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
            Platform.Windows.TrayIcon.ClearPairBalloon,
            Platform.Windows.TrayIcon.ShowNoticeBalloon,
            Platform.Windows.TrayIcon.ShowUpdateReadyBalloon,
            () => Platform.Windows.TrayIcon.OpenLocalWindow(),
            ShowUpdaterWindow).Register(handlerRegistry);
        // Runs in the user session, so this set lands on the clipboard the
        // user actually pastes from (the service's Session-0 one is invisible).
        new ClipboardHandler(new Platform.Clipboard.WindowsClipboardProvider().SetText).Register(handlerRegistry);
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
        new SystemHandler().Register(handlerRegistry);
        new ProfileListHandler(payload =>
        {
            var items = new List<(string Id, string Name)>(payload.Profiles.Count);
            foreach (var p in payload.Profiles)
            {
                items.Add((p.Id, p.Name));
            }
            s_profiles = new ProfileSnapshot(items, payload.ActiveId);
        }).Register(handlerRegistry);

        Platform.Windows.TrayIcon.ConfigureProfiles(
            getProfiles: () =>
            {
                var s = s_profiles;
                return (s.Items, s.ActiveId);
            },
            onSwitchProfile: id =>
            {
                try
                {
                    _ = outbound.SendAsync(
                        type: "profiles.switch",
                        payload: new ProfileSwitchPayload { Id = id },
                        payloadType: AppJsonContext.Default.ProfileSwitchPayload);
                }
                catch { /* best-effort */ }
            });

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

    // HTA caption is a known constant so the helper can close it by title.
    // The HTA content is English-only; HTA files cannot use the service i18n bundle.
    private static void ShowUpdaterWindow(string fromVersion, string toVersion)
    {
        try
        {
            var from = HtmlEscape(fromVersion);
            var to = HtmlEscape(toVersion);
            // Verbatim string (not a raw/interpolated literal): doubled quotes,
            // single braces, and __FROM__/__TO__ placeholders substituted below.
            // Avoids interpolated-raw-string parsing differences across compilers.
            var hta = @"<html>
<head>
<hta:application
  id=""nexusUpdater""
  applicationname=""Nexus Updater""
  caption=""yes""
  border=""thin""
  sysmenu=""no""
  maximizebutton=""no""
  minimizebutton=""no""
  showintaskbar=""yes""
  singleinstance=""yes""
  scroll=""no"" />
<title>Nexus Updater</title>
<style>
* { margin:0; padding:0; box-sizing:border-box; }
body { background:#1a1a1f; color:#e2e2e6; font-family:'Segoe UI',sans-serif; display:flex; align-items:center; justify-content:center; height:100vh; }
.card { background:#25252d; border-radius:10px; padding:32px 36px; width:340px; text-align:center; }
h1 { font-size:18px; font-weight:600; margin-bottom:8px; }
.ver { font-size:13px; color:#a0a0a8; margin-bottom:24px; }
.bar-track { background:#3a3a45; border-radius:4px; height:6px; overflow:hidden; margin-bottom:20px; }
.bar-fill { height:100%; width:40%; background:#6c6cff; border-radius:4px; animation:slide 1.4s ease-in-out infinite; }
@keyframes slide { 0%{transform:translateX(-100%)} 100%{transform:translateX(350%)} }
.note { font-size:12px; color:#7a7a88; line-height:1.5; }
</style>
</head>
<body>
<div class=""card"">
  <h1>Updating Nexus</h1>
  <div class=""ver"">__FROM__ &#x2192; __TO__</div>
  <div class=""bar-track""><div class=""bar-fill""></div></div>
  <div class=""note"">This takes about a minute. Nexus will reopen automatically.</div>
</div>
<script language=""VBScript"">
Sub Window_OnLoad
  window.resizeTo 380, 230
  window.moveTo (screen.availWidth - 380) / 2, (screen.availHeight - 230) / 2
End Sub
</script>
<script language=""JavaScript"">
setTimeout(function() { window.close(); }, 300000);
</script>
</body>
</html>".Replace("__FROM__", from).Replace("__TO__", to);
            var htaPath = Path.Combine(Path.GetTempPath(), "nexus-updating.hta");
            File.WriteAllText(htaPath, hta, Encoding.UTF8);
            // Full path: the helper's spawned environment may not have System32
            // on PATH, so a bare "mshta.exe" Start can fail silently.
            var mshta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mshta.exe");
            var psi = new ProcessStartInfo(mshta, $"\"{htaPath}\"")
            {
                UseShellExecute = true,
                CreateNoWindow = false,
            };
            var proc = Process.Start(psi);
            Diag($"ShowUpdaterWindow: mshta started pid={proc?.Id.ToString() ?? "null"} hta={htaPath}");
            // mshta spawns the window behind the dashboard. Poll for it (it
            // appears a moment after launch) and force it topmost + foreground
            // so it is visible over everything during the install.
            _ = Task.Run(() =>
            {
                for (int i = 0; i < 50; i++)
                {
                    var hwnd = FindWindowW(null, UpdaterWindowTitle);
                    if (hwnd != IntPtr.Zero)
                    {
                        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
                        SetForegroundWindow(hwnd);
                        return;
                    }
                    Thread.Sleep(100);
                }
            });
        }
        catch (Exception ex) { Diag($"ShowUpdaterWindow failed: {ex.Message}"); }
    }

    private static string HtmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static void CloseUpdaterWindow()
    {
        try
        {
            var hwnd = FindWindowW(null, UpdaterWindowTitle);
            if (hwnd != IntPtr.Zero)
            {
                PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch { /* best-effort */ }
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpShowWindow = 0x0040;

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
