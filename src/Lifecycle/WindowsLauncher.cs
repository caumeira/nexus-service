#if WINDOWS
using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;

namespace Qos.Service.Lifecycle;

/// <summary>
/// Default Qos.exe no-args entrypoint. Detects current install state and
/// dispatches accordingly. The launcher NEVER starts the daemon in-process
/// and (other than the first-time install path) NEVER triggers a UAC
/// prompt - the SERVICE_START DACL granted to Authenticated Users at
/// install time lets us recover a stopped service without elevation.
///
/// State machine:
///
///   Query SCM: is QosService registered?
///     No  -> self-elevate, run --install on self (the ONLY UAC path).
///     Yes -> Query status.
///            Running       -> spawn --tray if not running, open dashboard.
///            StartPending  -> wait briefly, retry, then dashboard.
///            Stopped       -> StartService() directly (DACL grant; no UAC),
///                             then open dashboard.
///            Other         -> attempt unprivileged StartService(); on failure
///                             open dashboard anyway and let the user see the
///                             service-down state.
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
                Console.WriteLine("[launcher] Qos is not installed yet; running --install");
                return WindowsServiceInstaller.RunInstall(Array.Empty<string>());

            case ServiceState.Running:
                Console.WriteLine("[launcher] service is running; opening dashboard");
                EnsureTrayRunning();
                OpenDashboard();
                return 0;

            case ServiceState.StartPending:
                Console.WriteLine("[launcher] service is starting; waiting briefly");
                WaitForState(ServiceState.Running, TimeSpan.FromSeconds(10));
                EnsureTrayRunning();
                OpenDashboard();
                return 0;

            case ServiceState.Stopped:
                Console.WriteLine("[launcher] service is stopped; starting unprivileged (DACL grant)");
                if (TryStartService())
                {
                    EnsureTrayRunning();
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
        // Use sc.exe rather than P/Invoke for AOT simplicity; one process
        // spawn per launcher run is fine.
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

    private static void OpenDashboard()
    {
        // Shell-execute the dashboard URL so it opens in the user's default
        // browser. This works from any session because we're already in the
        // user's interactive session (this is the launcher, not the service).
        try
        {
            var psi = new ProcessStartInfo($"http://localhost:{DefaultPort}/")
            {
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[launcher] failed to open dashboard: {ex.Message}");
        }
    }

    private static void EnsureTrayRunning()
    {
        // Best-effort: spawn Qos.exe --tray if no tray is alive in this
        // session. The tray's per-session mutex handles deduplication, so a
        // racing second spawn just exits silently.
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return;
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--tray");
            Process.Start(psi);
        }
        catch
        {
            // Non-fatal.
        }
    }
}
#endif
