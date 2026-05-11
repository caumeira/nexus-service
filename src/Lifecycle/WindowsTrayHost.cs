#if WINDOWS
using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;

namespace Qos.Service.Lifecycle;

/// <summary>
/// Entry point for <c>Qos.exe --tray</c>: lightweight user-session
/// process that owns the system tray icon (and, later, the desktop
/// widgets). It is NOT the daemon - the daemon runs as a LocalSystem
/// Windows Service in Session 0 and is invisible to interactive users.
///
/// Single-instance per logon session via <c>Local\QosTray</c> mutex.
/// If a second --tray launches in the same session, it just exits.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsTrayHost
{
    private const int DefaultPort = 9400;
    private const string SessionMutexName = @"Local\QosTray";

    private static readonly ManualResetEventSlim s_exitEvent = new(initialState: false);

    public static int Run(string[] args)
    {
        Diag("WindowsTrayHost.Run entered");
        if (!OperatingSystem.IsWindows()) return 0;

        // Per-session single-instance. The Local\ namespace is automatically
        // scoped to the current logon session, so two different users on the
        // same machine can each run their own tray independently.
        using var mutex = new Mutex(initiallyOwned: true, name: SessionMutexName, out var isFirst);
        if (!isFirst)
        {
            Console.WriteLine("[tray] another tray instance is already running in this session; exiting");
            return 0;
        }

        // Tray follows the service: if the service is stopped (e.g., the
        // user disabled "Start Qos at system startup", or hit the Stop
        // button in Settings), the tray has nothing to surface, so exit
        // immediately. We deliberately do NOT auto-start the service here -
        // that would defeat the user's choice.
        if (!IsServiceRunning())
        {
            Console.WriteLine("[tray] QosService not running; tray exits");
            return 0;
        }

        // onExit is only invoked by the watchdog now (service stopped
        // externally). The tray's menu no longer has a "Hide tray" entry -
        // dismissing the tray icon is a dashboard concern. So onExit just
        // signals exit; it does NOT touch ShowWindowsTrayIcon (the user
        // didn't ask to disable the tray, the service died).
        Platform.Windows.TrayIcon.Configure(
            DefaultPort,
            onExit: () => s_exitEvent.Set());

        Diag("calling TrayIcon.SetVisible(true)");
        Platform.Windows.TrayIcon.SetVisible(true);
        Diag("SetVisible returned");

        // Background watchdog: when the service stops (Stop button in
        // Settings, schtasks reboot, etc.) the tray icon should vanish on
        // its own. Polls SCM every 5s; calls onExit on first STOPPED read.
        var watchdog = new Thread(() =>
        {
            while (!s_exitEvent.IsSet)
            {
                System.Threading.Thread.Sleep(5000);
                if (s_exitEvent.IsSet) return;
                if (!IsServiceRunning())
                {
                    Console.WriteLine("[tray] QosService stopped externally; tray exits");
                    s_exitEvent.Set();
                    return;
                }
            }
        }) { IsBackground = true };
        watchdog.Start();

        // Tray runs on a background STA thread (see TrayIcon.cs). Keep the
        // main thread alive until the user picks Hide tray from the menu
        // or the watchdog detects the service stopped.
        s_exitEvent.Wait();

        Platform.Windows.TrayIcon.SetVisible(false);
        return 0;
    }

    private static void Diag(string msg)
    {
        // Direct write to a known absolute path. Bypasses TEMP resolution
        // and AppendAllText quirks - just FileStream.Write with autoflush.
        try
        {
            using var fs = new System.IO.FileStream(
                @"C:\Users\Public\qos-tray-debug.log",
                System.IO.FileMode.Append,
                System.IO.FileAccess.Write,
                System.IO.FileShare.ReadWrite);
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{System.Diagnostics.Process.GetCurrentProcess().Id}] {msg}\n";
            var bytes = System.Text.Encoding.UTF8.GetBytes(line);
            fs.Write(bytes, 0, bytes.Length);
        }
        catch { }
    }

    private static bool IsServiceRunning()
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
            if (p is null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            if (p.ExitCode != 0) return false;
            return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
#endif
