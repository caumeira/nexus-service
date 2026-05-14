using System;
using System.Collections.Generic;
using System.IO;

namespace Qos.Service.Widgets;

/// <summary>
/// Per-OS install roots scanned by <see cref="WidgetRegistry"/>. Precedence
/// matches <c>plans/widget-sdk.md</c>: user installs (signed) shadow bundled
/// (signed); dev installs (unsigned) shadow both but render a dev banner.
/// </summary>
/// <remarks>
/// Phase 0 only does discovery and serving. Signing verification, install
/// writes, and dev-banner rendering all land in Phase 3.
/// </remarks>
public static class WidgetInstallPaths
{
    public enum Source
    {
        /// <summary><c>widgets-dev/</c> - unpacked local copies authors symlink for iteration.</summary>
        Dev,
        /// <summary><c>widgets/</c> in the user profile.</summary>
        User,
        /// <summary><c>widgets/</c> next to the service binary.</summary>
        Bundled,
    }

    public readonly record struct Root(string Path, Source Source);

    /// <summary>
    /// All install roots that may contain widget directories, ordered such
    /// that earlier roots shadow later roots for the same widget id.
    /// </summary>
    public static IReadOnlyList<Root> Enumerate(string? baseDir = null)
    {
        var roots = new List<Root>();
        var appData = ResolveAppData();
        if (!string.IsNullOrEmpty(appData))
        {
            roots.Add(new Root(Path.Combine(appData, "widgets-dev"), Source.Dev));
            roots.Add(new Root(Path.Combine(appData, "widgets"), Source.User));
        }
        var bundled = string.IsNullOrEmpty(baseDir) ? AppContext.BaseDirectory : baseDir;
        roots.Add(new Root(Path.Combine(bundled, "widgets"), Source.Bundled));
        return roots;
    }

    private static string ResolveAppData()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrEmpty(appData) ? "" : Path.Combine(appData, "Qos");
        }
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? "";
            return string.IsNullOrEmpty(home) ? "" : Path.Combine(home, "Library", "Application Support", "Qos");
        }
        // Linux: XDG_DATA_HOME or ~/.local/share/Qos
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg)) return Path.Combine(xdg, "Qos");
        var linHome = Environment.GetEnvironmentVariable("HOME") ?? "";
        return string.IsNullOrEmpty(linHome) ? "" : Path.Combine(linHome, ".local", "share", "Qos");
    }
}
