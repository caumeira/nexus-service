using System;
using System.Diagnostics;
using System.IO;
using Nexus.Service.Platform;

namespace Nexus.Service.Diagnostics;

/// <summary>
/// Opens the Nexus logs folder (service.log, plus desktop-host.log on Windows)
/// in the OS file manager. Must run in a context that owns a desktop: on Windows
/// the LocalSystem service is in Session 0 and cannot show a window, so it
/// delegates here over the helper pipe (<c>diagnostics.openLogs</c>) and the
/// user-session helper runs Open(). macOS/Linux run in the user session already,
/// so the route calls Open() directly.
/// </summary>
internal static class LogsFolder
{
    public static void Open()
    {
        var dir = ServiceLog.LogsDirectory;
        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo { UseShellExecute = true };
        if (OperatingSystem.IsWindows())
        { psi.FileName = "explorer.exe"; psi.Arguments = $"\"{dir}\""; }
        else if (OperatingSystem.IsMacOS())
        { psi.FileName = "open"; psi.Arguments = $"\"{dir}\""; psi.UseShellExecute = false; }
        else
        { psi.FileName = "xdg-open"; psi.Arguments = $"\"{dir}\""; psi.UseShellExecute = false; }
        Process.Start(psi);
    }
}
