using System;
using System.IO;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Chromium-family binary probe order, shared by the dashboard launcher
/// (<see cref="LinuxTrayHost"/>) and the panel kiosk host. --app / --kiosk
/// windows require a Chromium engine; Firefox and the portal openers are
/// dashboard-only fallbacks and stay in the tray's list.
/// </summary>
internal static class LinuxBrowsers
{
    // Computed per call: HOME is adopted from the active session after the
    // root daemon starts, so a type-init snapshot could point at /root.
    public static string[] ChromiumFamily()
    {
        var userFlatpakBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "flatpak", "exports", "bin");
        const string sysFlatpakBin = "/var/lib/flatpak/exports/bin";
        return new[]
        {
            Path.Combine(userFlatpakBin, "org.chromium.Chromium"),
            sysFlatpakBin + "/org.chromium.Chromium",
            Path.Combine(userFlatpakBin, "com.google.Chrome"),
            sysFlatpakBin + "/com.google.Chrome",
            Path.Combine(userFlatpakBin, "com.brave.Browser"),
            sysFlatpakBin + "/com.brave.Browser",
            sysFlatpakBin + "/com.microsoft.Edge",
            "/usr/bin/chromium",
            "/usr/bin/chromium-browser",
            "/usr/bin/google-chrome",
            "/usr/bin/brave-browser",
        };
    }

    public static string? FindChromium()
    {
        foreach (var path in ChromiumFamily())
        {
            if (File.Exists(path))
                return path;
        }
        return null;
    }
}
