#if WINDOWS
using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Default Nexus.exe no-args entrypoint. Detects current install state and
/// dispatches accordingly. The launcher NEVER starts the daemon in-process
/// and (other than the first-time install path) NEVER triggers a UAC
/// prompt - the SERVICE_START DACL granted to Authenticated Users at
/// install time lets us recover a stopped service without elevation.
///
/// State machine:
///
///   Query SCM: is NexusService registered?
///     No  -> self-elevate, run --install on self (the ONLY UAC path).
///     Yes -> Query status.
///            Running       -> spawn --helper if needed, open dashboard. If
///                             /ping stays silent and SCM no longer reports
///                             RUNNING, it stopped under us: wait for STOPPED
///                             and restart fresh (falls into Stopped).
///            StartPending  -> wait briefly, retry, then dashboard.
///            StopPending   -> a relaunch landed mid-shutdown; wait for STOPPED
///                             then start fresh, so we never open onto a dead
///                             port (the blank-dashboard bug).
///            Stopped       -> StartService() directly (DACL grant; no UAC),
///                             then open dashboard.
///            Other         -> log the state and open the dashboard so the user
///                             sees the service-down banner; state is ambiguous
///                             so we don't try to start.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsLauncher
{
    private const int DefaultPort = 9400;

    public static int Run()
    {
        if (!OperatingSystem.IsWindows()) return 0;

        var state = QueryServiceState();
        switch (state)
        {
            case ServiceState.NotInstalled:
                // First-time install: this is the only path that prompts
                // UAC. --install handles the self-elevate internally.
                Console.WriteLine("[launcher] Nexus is not installed yet; running --install");
                return WindowsServiceInstaller.RunInstall(Array.Empty<string>());

            case ServiceState.Running:
                Console.WriteLine("[launcher] service is running; opening dashboard");
                EnsureHelperRunning();
                // Was RUNNING at query time but /ping never answered AND SCM no
                // longer reports RUNNING -> it shut down under us (the user hit
                // "Shut down" then immediately reopened). Opening now points Edge
                // at a dead port -> blank page; wait for the stop to finish and
                // restart instead. The state re-check keeps a slow-but-healthy
                // boot (ping slow, still RUNNING) on the plain open-dashboard path.
                if (!WaitForPing(TimeSpan.FromSeconds(10)) && QueryServiceState() != ServiceState.Running)
                {
                    Console.WriteLine("[launcher] service stopped under us; waiting for stop, then restarting");
                    WaitForState(ServiceState.Stopped, TimeSpan.FromSeconds(20));
                    goto case ServiceState.Stopped;
                }
                OpenDashboard();
                return 0;

            case ServiceState.StartPending:
                Console.WriteLine("[launcher] service is starting; waiting briefly");
                WaitForState(ServiceState.Running, TimeSpan.FromSeconds(10));
                EnsureHelperRunning();
                WaitForPing(TimeSpan.FromSeconds(10));
                OpenDashboard();
                return 0;

            case ServiceState.StopPending:
                // Relaunch landed mid-shutdown (tray "Shut down" then reopen).
                // Opening the dashboard now points Edge at a dying port -> blank
                // page. Wait for the stop to settle, then start fresh.
                Console.WriteLine("[launcher] service is stopping; waiting for stop, then restarting");
                WaitForState(ServiceState.Stopped, TimeSpan.FromSeconds(20));
                goto case ServiceState.Stopped;

            case ServiceState.Stopped:
                Console.WriteLine("[launcher] service is stopped; starting unprivileged (DACL grant)");
                if (TryStartService())
                {
                    EnsureHelperRunning();
                    // sc.exe start returns when SCM accepts the request, and our
                    // ServiceMain sets RUNNING as soon as we hand the web app off
                    // to a worker thread - the port isn't actually bound yet. Wait
                    // for /ping so the browser opens against a ready service.
                    WaitForPing(TimeSpan.FromSeconds(15));
                    OpenDashboard();
                    return 0;
                }
                Console.Error.WriteLine("[launcher] could not start service; opening dashboard anyway");
                OpenDashboard();
                return 1;

            default:
                Console.Error.WriteLine($"[launcher] unexpected service state: {state}");
                OpenDashboard();
                return 1;
        }
    }

    private enum ServiceState
    {
        NotInstalled,
        Stopped,
        StartPending,
        StopPending,
        Running,
        Other,
    }

    private static ServiceState QueryServiceState()
    {
        // sc.exe rather than P/Invoke: AOT-safe, one spawn per launcher run.
        var psi = new ProcessStartInfo("sc.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("query");
        psi.ArgumentList.Add(WindowsServiceInstaller.ServiceName);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return ServiceState.NotInstalled;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            // sc query returns 1060 / "service does not exist" when not installed.
            if (p.ExitCode != 0) return ServiceState.NotInstalled;
            // Parse STATE line. Example: "        STATE              : 4  RUNNING"
            if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) return ServiceState.Running;
            if (output.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase)) return ServiceState.StartPending;
            if (output.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase)) return ServiceState.StopPending;
            if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return ServiceState.Stopped;
            return ServiceState.Other;
        }
        catch
        {
            return ServiceState.NotInstalled;
        }
    }

    private static bool TryStartService()
    {
        var psi = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("start");
        psi.ArgumentList.Add(WindowsServiceInstaller.ServiceName);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(10000);
            if (p.ExitCode != 0) return false;
            return WaitForState(ServiceState.Running, TimeSpan.FromSeconds(15));
        }
        catch { return false; }
    }

    private static bool WaitForState(ServiceState target, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (QueryServiceState() == target) return true;
            Thread.Sleep(500);
        }
        return false;
    }

    private static bool WaitForPing(TimeSpan timeout)
    {
        // Even when SCM reports RUNNING, our ServiceMain set that state right
        // after handing off the web app to a worker thread - Kestrel may not
        // have bound :9400 yet. Hit /ping until we get a 2xx (or give up).
        var deadline = DateTime.UtcNow + timeout;
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var resp = http.GetAsync($"http://localhost:{DefaultPort}/ping").GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode) return true;
            }
            catch { }
            Thread.Sleep(300);
        }
        return false;
    }

    private static void OpenDashboard()
    {
        // Open the dashboard in an Edge --app frameless window (the same
        // path the tray's "Open Nexus" menu uses). Falls back to the user's
        // default browser if Edge isn't found.
        try
        {
            Platform.Windows.TrayIcon.OpenLocalWindow(DefaultPort);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[launcher] OpenLocalWindow failed, falling back to browser: {ex.Message}");
            try
            {
                var psi = new ProcessStartInfo($"http://localhost:{DefaultPort}/")
                {
                    UseShellExecute = true,
                };
                Process.Start(psi);
            }
            catch (Exception ex2)
            {
                Console.Error.WriteLine($"[launcher] failed to open dashboard: {ex2.Message}");
            }
        }
    }

    private static void EnsureHelperRunning()
    {
        // Best-effort: spawn Nexus.exe --helper if no helper is alive in this
        // session. The helper's per-session mutex (Local\NexusHelper) handles
        // deduplication, so a racing second spawn just exits silently.
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return;
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--helper");
            Process.Start(psi);
        }
        catch
        {
            // Non-fatal.
        }
    }
}
#endif
