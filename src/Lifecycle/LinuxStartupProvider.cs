using System;
using System.IO;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Real Linux autostart via the XDG autostart spec: writes/removes
/// <c>$XDG_CONFIG_HOME/autostart/nexus.desktop</c> (default
/// <c>~/.config/autostart/</c>), which every desktop session launches at login.
/// This backs the in-app "start at login" toggle. The tarball installer also
/// installs a <c>systemd --user</c> unit as the primary mechanism; the two
/// coexist (systemd starts it; this toggle is the fallback / user-visible
/// switch). Pure file IO — AOT-safe.
/// </summary>
public sealed class LinuxStartupProvider : IStartupProvider
{
    private const string DesktopFileName = "nexus.desktop";

    private static string AutostartPath =>
        Path.Combine(ConfigHome(), "autostart", DesktopFileName);

    public bool IsEnabled()
    {
        if (!OperatingSystem.IsLinux())
            return false;
        return File.Exists(AutostartPath);
    }

    public bool SetEnabled(bool enabled, string path, string arguments)
    {
        if (!OperatingSystem.IsLinux())
            return false;

        try
        {
            if (!enabled)
            {
                if (File.Exists(AutostartPath))
                    File.Delete(AutostartPath);
                return true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(AutostartPath)!);
            var exec = string.IsNullOrWhiteSpace(arguments) ? Quote(path) : $"{Quote(path)} {arguments}";
            var content =
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=Nexus\n" +
                "Comment=Nexus hardware monitoring and control service\n" +
                $"Exec={exec}\n" +
                "Icon=nexus\n" +
                "Terminal=false\n" +
                "X-GNOME-Autostart-enabled=true\n";
            File.WriteAllText(AutostartPath, content);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[linux-startup] failed: {ex.Message}");
            return false;
        }
    }

    private static string ConfigHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdg))
            return xdg;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    }

    private static string Quote(string p) => p.Contains(' ') ? $"\"{p}\"" : p;
}
