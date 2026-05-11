#if WINDOWS
using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;

namespace Qos.Service.Lifecycle;

/// <summary>
/// Entry point for <c>qOS.exe --tray</c>: lightweight user-session
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

        // Make sure the service is up before we go interactive. If it's
        // stopped, kick it (works without UAC because --install granted
        // SERVICE_START to Authenticated Users).
        EnsureServiceRunning();

        Platform.Windows.TrayIcon.Configure(
            DefaultPort,
            onExit: () => s_exitEvent.Set());

        Platform.Windows.TrayIcon.SetVisible(true);

        // Tray runs on a background STA thread (see TrayIcon.cs). Keep the
        // main thread alive until the user picks Exit from the menu.
        s_exitEvent.Wait();

        Platform.Windows.TrayIcon.SetVisible(false);
        return 0;
    }

    private static void EnsureServiceRunning()
    {
        // Best-effort: query the service controller status and start the
        // service if it's stopped. Any failure here is non-fatal - the user
        // can still open the dashboard, which will simply fail to connect
        // until the service comes up.
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
            if (p is null) return;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            if (p.ExitCode != 0) return;
            if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
            {
                // Stopped: try to start. No UAC needed thanks to the DACL grant.
                var startPsi = new ProcessStartInfo("sc.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                startPsi.ArgumentList.Add("start");
                startPsi.ArgumentList.Add(WindowsServiceInstaller.ServiceName);
                using var sp = Process.Start(startPsi);
                sp?.WaitForExit(10000);
            }
        }
        catch
        {
            // Non-fatal.
        }
    }
}
#endif
