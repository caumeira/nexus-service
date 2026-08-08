using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

public sealed class LinuxShortcutsProvider : IShortcutsProvider
{
    private static readonly TimeSpan AppListCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IconCacheTtl = TimeSpan.FromHours(1);

    private List<Shortcut>? _appCache;
    private DateTime _appCacheExpiry;
    private readonly object _appLock = new();

    private readonly Dictionary<string, (byte[] Data, DateTime Expiry)> _iconCache = new();
    private readonly object _iconLock = new();
    private readonly Dictionary<string, string> _iconNames = new();
    private readonly object _iconNameLock = new();

    public IReadOnlyList<Shortcut> GetAll()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return Array.Empty<Shortcut>();

        lock (_appLock)
        {
            if (_appCache is not null && _appCacheExpiry > DateTime.UtcNow)
                return _appCache;
        }

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                ?? Path.Combine(home, ".local", "share");

            var searchDirs = new[]
            {
                Path.Combine(xdgDataHome, "applications"),
                "/usr/share/applications",
                "/usr/local/share/applications",
                "/var/lib/flatpak/exports/share/applications",
                "/var/lib/snapd/desktop/applications",
            };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var apps = new List<Shortcut>();

            foreach (var dir in searchDirs)
            {
                if (!Directory.Exists(dir))
                    continue;

                try
                {
                    foreach (var file in Directory.GetFiles(dir, "*.desktop", SearchOption.TopDirectoryOnly))
                    {
                        var filename = Path.GetFileNameWithoutExtension(file);
                        if (!seen.Add(filename))
                            continue;

                        var entry = ParseDesktopFile(file, out var iconName);
                        if (entry is null)
                            continue;

                        apps.Add(entry);
                        if (!string.IsNullOrEmpty(iconName))
                        {
                            lock (_iconNameLock)
                            {
                                _iconNames[entry.Id] = iconName;
                            }
                        }
                    }
                }
                catch { }
            }

            var sorted = apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();

            lock (_appLock)
            {
                _appCache = sorted;
                _appCacheExpiry = DateTime.UtcNow + AppListCacheTtl;
            }

            return sorted;
        }
        catch
        {
            return Array.Empty<Shortcut>();
        }
    }

    public Shortcut? GetById(string targetId)
    {
        return GetAll().FirstOrDefault(s =>
            string.Equals(s.Id, targetId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Name, targetId, StringComparison.OrdinalIgnoreCase));
    }

    public byte[] GetIcon(string targetId)
    {
        lock (_iconLock)
        {
            if (_iconCache.TryGetValue(targetId, out var cached) && cached.Expiry > DateTime.UtcNow)
                return cached.Data;
        }

        var shortcut = GetById(targetId);
        if (shortcut is null)
            return Array.Empty<byte>();

        try
        {
            string? iconName;
            lock (_iconNameLock)
            {
                _iconNames.TryGetValue(targetId, out iconName);
            }
            iconName ??= GetIconFieldFromDesktop(shortcut.Path);
            if (string.IsNullOrEmpty(iconName))
                return Array.Empty<byte>();

            var iconBytes = ResolveIcon(iconName);

            if (iconBytes.Length > 0)
            {
                lock (_iconLock)
                {
                    _iconCache[targetId] = (iconBytes, DateTime.UtcNow + IconCacheTtl);
                }
            }

            return iconBytes;
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    public bool Launch(string targetId)
    {
        var shortcut = GetById(targetId);
        if (shortcut is null)
            return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gio",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("launch");
            psi.ArgumentList.Add(shortcut.Path);

            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static Shortcut? ParseDesktopFile(string path, out string? iconName)
    {
        iconName = null;
        try
        {
            var lines = File.ReadAllLines(path);
            var inDesktopEntry = false;
            string? name = null;
            string? type = null;
            var noDisplay = false;
            var hidden = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('['))
                {
                    inDesktopEntry = trimmed == "[Desktop Entry]";
                    continue;
                }

                if (!inDesktopEntry || trimmed.StartsWith('#') || !trimmed.Contains('='))
                    continue;

                var eqIdx = trimmed.IndexOf('=');
                var key = trimmed[..eqIdx].Trim();
                var value = trimmed[(eqIdx + 1)..].Trim();

                switch (key)
                {
                    case "Name" when name is null:
                        name = value;
                        break;
                    case "Type":
                        type = value;
                        break;
                    case "Icon":
                        iconName = value;
                        break;
                    case "NoDisplay":
                        noDisplay = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "Hidden":
                        hidden = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                        break;
                }
            }

            if (type != "Application" || noDisplay || hidden || string.IsNullOrEmpty(name))
                return null;

            if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                return null;

            return new Shortcut
            {
                Id = Path.GetFileNameWithoutExtension(path),
                Name = name,
                Path = path,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? GetIconFieldFromDesktop(string desktopPath)
    {
        try
        {
            foreach (var line in File.ReadLines(desktopPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Icon=", StringComparison.Ordinal))
                    return trimmed[5..].Trim();
                if (trimmed.StartsWith('[') && trimmed != "[Desktop Entry]")
                    break;
            }
        }
        catch { }
        return null;
    }

    private static byte[] ResolveIcon(string iconNameOrPath)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var themeBases = LinuxIconThemeResolver.DefaultThemeBases(home);
        var pixmapDirs = LinuxIconThemeResolver.DefaultPixmapDirs;

        var png = LinuxIconThemeResolver.ResolvePng(iconNameOrPath, themeBases, pixmapDirs);
        if (png.Length > 0)
            return png;

        // No PNG anywhere in the theme - fall back to rasterizing an SVG,
        // which the shared PNG-only resolver deliberately never attempts.
        if (iconNameOrPath.StartsWith('/'))
        {
            return iconNameOrPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) && File.Exists(iconNameOrPath)
                ? ConvertSvgToPng(iconNameOrPath)
                : Array.Empty<byte>();
        }

        foreach (var basePath in themeBases)
        {
            var svgPath = Path.Combine(basePath, "scalable", "apps", $"{iconNameOrPath}.svg");
            if (File.Exists(svgPath))
                return ConvertSvgToPng(svgPath);
        }

        foreach (var dir in pixmapDirs)
        {
            var svgPath = Path.Combine(dir, $"{iconNameOrPath}.svg");
            if (File.Exists(svgPath))
                return ConvertSvgToPng(svgPath);
        }

        return Array.Empty<byte>();
    }

    private static byte[] ConvertSvgToPng(string svgPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "rsvg-convert",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add("128");
            psi.ArgumentList.Add("-h");
            psi.ArgumentList.Add("128");
            psi.ArgumentList.Add(svgPath);

            using var proc = Process.Start(psi);
            if (proc is null)
                return Array.Empty<byte>();

            using var ms = new MemoryStream();
            proc.StandardOutput.BaseStream.CopyTo(ms);
            proc.WaitForExit(5000);

            return proc.ExitCode == 0 ? ms.ToArray() : Array.Empty<byte>();
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }
}
