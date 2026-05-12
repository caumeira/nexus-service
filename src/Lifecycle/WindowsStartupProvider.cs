using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

#if WINDOWS
using Microsoft.Win32;
#endif

namespace Qos.Service.Lifecycle;

/// <summary>
/// Per-user "start at logon" toggle for the Qos user-session helper. The
/// daemon itself runs as a LocalSystem Windows Service from boot, so it
/// doesn't need a startup hook. This provider only controls whether the
/// helper companion (Qos.exe --helper) auto-launches at sign-in.
///
/// Backed by <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Qos</c>.
/// HKCU is per-user and writable without elevation, so the dashboard can
/// flip the toggle on/off without UAC.
///
/// Legacy: prior versions registered a logon-triggered scheduled task
/// (<c>QosService</c>) under the user's identity to autostart the daemon.
/// That task is no longer needed and is cleaned up here on first run.
/// </summary>
public sealed class WindowsStartupProvider : IStartupProvider
{
#if WINDOWS
    private const string HkcuRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Qos";
    private const string LegacySchtaskName = "QosService";
    private const string LegacySchtaskAlternate = "Qos";

    private static bool _legacyCleanupDone;
#endif

    public bool IsEnabled()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
#if WINDOWS
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(HkcuRunKey, writable: false);
            if (key is null) return false;
            return key.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch { return false; }
#else
        return false;
#endif
    }

    public bool SetEnabled(bool enabled, string path, string arguments)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
#if WINDOWS
        try
        {
            CleanupLegacySchtaskOnce();

            using var key = Registry.CurrentUser.CreateSubKey(HkcuRunKey, writable: true);
            if (key is null) return false;

            if (enabled)
            {
                // path is the daemon's installed EXE. For helper autostart we
                // always want --helper mode regardless of any extra arguments
                // the caller passes.
                var command = $"\"{path}\" --helper";
                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch { return false; }
#else
        return false;
#endif
    }

#if WINDOWS
    private static void CleanupLegacySchtaskOnce()
    {
        if (_legacyCleanupDone) return;
        _legacyCleanupDone = true;
        // Best-effort: drop any leftover logon schtask from the pre-service
        // era. Ignored failures (task absent, no permission, etc.) are fine
        // because the SCM service supersedes whatever the task would have
        // done anyway.
        try { RunSchtasks("/Delete", "/TN", LegacySchtaskName, "/F"); } catch { }
        try { RunSchtasks("/Delete", "/TN", LegacySchtaskAlternate, "/F"); } catch { }
    }

    [SupportedOSPlatform("windows")]
    private static void RunSchtasks(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("schtasks")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi);
        if (p is null) return;
        if (!p.WaitForExit(5000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }
    }
#endif
}
