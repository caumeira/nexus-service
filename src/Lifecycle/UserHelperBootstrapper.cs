#if WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Spawns <c>Nexus.exe --helper</c> in the active console user's session when
/// the LocalSystem service comes up. Necessary because the service is
/// LocalSystem in Session 0 - it can't draw a tray icon, can't see the
/// foreground window, can't query SMTC media, etc. The helper lives in the
/// user session and does all of that on the service's behalf.
///
/// Unconditional: no <c>ShowWindowsTrayIcon</c> gate. That preference controls
/// tray-icon visibility only; the helper hosts more than the tray, so it runs
/// regardless.
///
/// Cross-session launch uses schtasks (Task Scheduler service handles the
/// session/profile setup that direct CreateProcessAsUser fails on).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UserHelperBootstrapper
{
    public static void EnsureLaunched()
    {
        try
        {
            SpawnInUserSession("--helper", "helper-bootstrap", "NexusHelperBootstrap");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[helper-bootstrap] failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Mirror of <see cref="EnsureLaunched"/> for the "open the dashboard window"
    /// hotline. The desktop widget context menu's "Open dashboard" entry hits
    /// <c>POST /service/open-app</c>; the service handler runs as LocalSystem
    /// in Session 0 and cannot spawn an interactive Edge --app on its own,
    /// so we delegate to a one-shot <c>Nexus.exe --open-app</c> in the active
    /// console session, which then runs the same Edge --app spawn path the
    /// helper uses.
    /// </summary>
    public static void LaunchOpenApp()
    {
        try
        {
            SpawnInUserSession("--open-app", "open-app", "NexusOpenApp");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[open-app] failed: {ex.Message}");
        }
    }

    private static void SpawnInUserSession(string nexusArg, string logTag, string taskPrefix)
    {
        var username = ResolveActiveConsoleUsername();
        if (string.IsNullOrEmpty(username))
        {
            Console.WriteLine($"[{logTag}] no active console user; skipping");
            return;
        }

        var exePath = Path.Combine(AppContext.BaseDirectory, "Nexus.exe");
        if (!File.Exists(exePath))
        {
            Console.Error.WriteLine($"[{logTag}] Nexus.exe not found at {exePath}");
            return;
        }

        var taskName = $"{taskPrefix}_{Environment.ProcessId}_{DateTime.UtcNow.Ticks}";
        if (!Schtasks("/Create", "/TN", taskName, "/TR", $"\"{exePath}\" {nexusArg}",
                      "/SC", "ONCE", "/ST", "23:59", "/RU", username, "/IT", "/F"))
        {
            return;
        }
        try { Schtasks("/Run", "/TN", taskName); }
        finally { Schtasks("/Delete", "/TN", taskName, "/F"); }

        Console.WriteLine($"[{logTag}] launched {nexusArg} as {username}");
    }

    private static string ResolveActiveConsoleUsername()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) return string.Empty;
        var buf = IntPtr.Zero;
        try
        {
            // WTSUserName = 5
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, 5, out buf, out _) || buf == IntPtr.Zero)
            {
                return string.Empty;
            }
            return Marshal.PtrToStringUni(buf) ?? string.Empty;
        }
        catch { return string.Empty; }
        finally { if (buf != IntPtr.Zero) WTSFreeMemory(buf); }
    }

    private static bool Schtasks(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(10000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer, uint sessionId, int infoClass, out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);
}
#endif
