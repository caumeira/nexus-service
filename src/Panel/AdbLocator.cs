namespace Nexus.Service.Panel;

/// <summary>
/// Resolves the adb executable shared by the USB-phone and Q-series watchers.
/// Prefers the copy bundled next to the service exe (Bundled/{rid}/adb/ lands
/// at {BaseDirectory}/tools/adb/) so the service works on a host with no Android SDK;
/// falls back to PATH then the Android SDK platform-tools dir for dev boxes.
/// </summary>
internal static class AdbLocator
{
    public static string? ResolveAdbPath()
    {
        var exe = OperatingSystem.IsWindows() ? "adb.exe" : "adb";

        // 1. Bundled copy next to the service exe (the shipped path).
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "adb", exe);
        if (File.Exists(bundled)) return bundled;

        // 2. PATH (build-pc convention + user platform-tools).
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate)) return candidate;
            }
        }

        // 3. Android SDK platform-tools (Android Studio default).
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                var candidate = Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                var macCandidate = Path.Combine(home, "Library", "Android", "sdk", "platform-tools", "adb");
                if (File.Exists(macCandidate)) return macCandidate;

                var linuxCandidate = Path.Combine(home, "Android", "Sdk", "platform-tools", "adb");
                if (File.Exists(linuxCandidate)) return linuxCandidate;
            }
        }

        return null;
    }
}
