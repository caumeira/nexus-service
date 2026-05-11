#if WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Qos.Service.Persistence;

namespace Qos.Service.Lifecycle;

/// <summary>
/// Spawns <c>Qos.exe --tray</c> in the active console user's session when
/// the service comes up. Necessary because the service is LocalSystem in
/// Session 0 - it can't draw a tray icon itself, and HKCU\Run only fires
/// at logon (not on service restart or post-install).
///
/// Gated by <see cref="UiSettings.ShowWindowsTrayIcon"/>: if the user has
/// the tray icon disabled in dashboard settings, we don't spawn.
///
/// Cross-session launch uses schtasks (Task Scheduler service handles the
/// session/profile setup that direct CreateProcessAsUser fails on).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TrayBootstrapper
{
    public static void TryLaunch(IConfigStore store)
    {
        try
        {
            if (!store.Load().Ui.ShowWindowsTrayIcon)
            {
                Console.WriteLine("[tray-bootstrap] ShowWindowsTrayIcon is false; skipping");
                return;
            }

            var username = ResolveActiveConsoleUsername();
            if (string.IsNullOrEmpty(username))
            {
                Console.WriteLine("[tray-bootstrap] no active console user; deferring to next logon");
                return;
            }

            var exePath = Path.Combine(AppContext.BaseDirectory, "Qos.exe");
            if (!File.Exists(exePath))
            {
                Console.Error.WriteLine($"[tray-bootstrap] Qos.exe not found at {exePath}");
                return;
            }

            // The tray binary has its own per-session mutex, so a double-
            // bootstrap (e.g. user is already logged in with a tray, and
            // we spawn another) is harmless - the second exits silently.
            // ProcessId alone collides if the service is fast-crash-looped
            // and a previous task wasn't /Delete'd cleanly; append Ticks.
            var taskName = $"QosTrayBootstrap_{Environment.ProcessId}_{DateTime.UtcNow.Ticks}";
            if (!Schtasks("/Create", "/TN", taskName, "/TR", $"\"{exePath}\" --tray",
                          "/SC", "ONCE", "/ST", "23:59", "/RU", username, "/IT", "/F"))
            {
                return;
            }
            try { Schtasks("/Run", "/TN", taskName); }
            finally { Schtasks("/Delete", "/TN", taskName, "/F"); }

            Console.WriteLine($"[tray-bootstrap] launched tray as {username}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tray-bootstrap] failed: {ex.Message}");
        }
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
