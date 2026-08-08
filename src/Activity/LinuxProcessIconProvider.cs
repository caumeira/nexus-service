using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Nexus.Service.Activity;

/// <summary>
/// Linux IProcessIconProvider: matches a running process's exe basename to a
/// .desktop file's Exec= command, then resolves that entry's Icon= through
/// the shared XDG theme lookup (LinuxIconThemeResolver). SVG-only icons are
/// skipped rather than rasterized - unlike LinuxShortcutsProvider, this path
/// runs for arbitrary live processes, and a bad/missing icon must never be
/// worse than no icon.
/// </summary>
public sealed class LinuxProcessIconProvider : IProcessIconProvider
{
    private static readonly TimeSpan IndexCacheTtl = TimeSpan.FromMinutes(5);

    // Argument-forwarding wrappers never match the real running process's
    // basename, so keying the map on one of these would misattribute icons
    // across every app that happens to launch through the same wrapper.
    private static readonly HashSet<string> WrapperBasenames = new(StringComparer.Ordinal)
    {
        "env", "sh", "bash", "flatpak",
    };

    private readonly object _lock = new();
    private Dictionary<string, string>? _execToIcon;
    private DateTime _indexExpiry;

    public byte[]? GetIcon(string exePath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || string.IsNullOrEmpty(exePath))
            return Array.Empty<byte>();

        var basename = Path.GetFileName(exePath);
        if (string.IsNullOrEmpty(basename))
            return Array.Empty<byte>();

        Dictionary<string, string> index;
        try
        {
            index = GetOrBuildIndex();
        }
        catch
        {
            // The index build itself faulted (e.g. a transient IO error) -
            // no verdict was reached, so this must not be cached as empty.
            return null;
        }

        if (!index.TryGetValue(basename, out var iconName) || string.IsNullOrEmpty(iconName))
            return Array.Empty<byte>();

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return LinuxIconThemeResolver.ResolvePng(
                iconName,
                LinuxIconThemeResolver.DefaultThemeBases(home),
                LinuxIconThemeResolver.DefaultPixmapDirs);
        }
        catch
        {
            return null;
        }
    }

    private Dictionary<string, string> GetOrBuildIndex()
    {
        lock (_lock)
        {
            if (_execToIcon is not null && _indexExpiry > DateTime.UtcNow)
                return _execToIcon;
        }

        var index = BuildExecIconMap(SearchDirs());

        lock (_lock)
        {
            _execToIcon = index;
            _indexExpiry = DateTime.UtcNow + IndexCacheTtl;
        }
        return index;
    }

    private static IEnumerable<string> SearchDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
            ?? Path.Combine(home, ".local", "share");

        yield return Path.Combine(xdgDataHome, "applications");
        yield return "/usr/share/applications";
        yield return "/usr/local/share/applications";
        yield return "/var/lib/flatpak/exports/share/applications";
        yield return "/var/lib/snapd/desktop/applications";
    }

    /// <summary>Maps each .desktop file's Exec= basename to its Icon= value
    /// across every dir in desktopDirs. Unlike LinuxShortcutsProvider's app
    /// list, entries are kept regardless of Type/NoDisplay/Hidden - a running
    /// process can back a helper or background .desktop entry that a
    /// launcher would never show. The first entry seen for a given basename
    /// wins, mirroring the search-dir precedence order.</summary>
    internal static Dictionary<string, string> BuildExecIconMap(IEnumerable<string> desktopDirs)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dir in desktopDirs)
        {
            if (!Directory.Exists(dir))
                continue;

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.desktop", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                var (execBasename, icon) = ParseDesktopEntry(file);
                if (execBasename is not null && icon is not null && !map.ContainsKey(execBasename))
                {
                    map[execBasename] = icon;
                }
            }
        }
        return map;
    }

    private static (string? ExecBasename, string? Icon) ParseDesktopEntry(string path)
    {
        try
        {
            var inDesktopEntry = false;
            string? execBasename = null;
            string? icon = null;

            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('['))
                {
                    // Only the [Desktop Entry] section's Exec is the main
                    // launch command - [Desktop Action ...] sections carry
                    // secondary commands (e.g. "New Window") that would
                    // misattribute the icon.
                    inDesktopEntry = trimmed == "[Desktop Entry]";
                    continue;
                }

                if (!inDesktopEntry || trimmed.StartsWith('#') || !trimmed.Contains('='))
                    continue;

                var eqIdx = trimmed.IndexOf('=');
                var key = trimmed[..eqIdx].Trim();
                var value = trimmed[(eqIdx + 1)..].Trim();

                if (key == "Exec" && execBasename is null)
                {
                    execBasename = ParseExecBasename(value);
                }
                else if (key == "Icon" && icon is null)
                {
                    icon = value;
                }
            }

            return (execBasename, icon);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>The launched binary's basename from a desktop Exec= value:
    /// the first whitespace-separated token (unquoted), directory prefix
    /// stripped. Field codes (%f, %U, ...) always follow the binary token so
    /// splitting on the first space is enough. Null for a known wrapper
    /// basename (see WrapperBasenames).</summary>
    internal static string? ParseExecBasename(string execValue)
    {
        var trimmed = execValue.Trim();
        if (trimmed.Length == 0)
            return null;

        string token;
        if (trimmed[0] == '"')
        {
            var end = trimmed.IndexOf('"', 1);
            token = end > 0 ? trimmed[1..end] : trimmed.Trim('"');
        }
        else
        {
            var spaceIdx = trimmed.IndexOf(' ');
            token = spaceIdx > 0 ? trimmed[..spaceIdx] : trimmed;
        }

        var basename = Path.GetFileName(token);
        if (string.IsNullOrEmpty(basename) || WrapperBasenames.Contains(basename))
            return null;
        return basename;
    }
}
