using System;
using System.Collections.Generic;
using System.IO;

namespace Nexus.Service.Activity;

/// <summary>
/// Resolves an XDG icon theme name (or an absolute icon path) to PNG bytes:
/// the hicolor theme tree searched by preferred size, then flat pixmap dirs.
/// Shared by LinuxShortcutsProvider (which layers its own SVG rasterization
/// on top of this) and LinuxProcessIconProvider (PNG-only). Never attempts
/// SVG conversion itself.
/// </summary>
internal static class LinuxIconThemeResolver
{
    internal static readonly string[] IconSizes = { "128x128", "96x96", "64x64", "48x48", "256x256" };

    internal static readonly string[] DefaultPixmapDirs = { "/usr/share/pixmaps" };

    internal static string[] DefaultThemeBases(string home) => new[]
    {
        "/usr/share/icons/hicolor",
        Path.Combine(home, ".local", "share", "icons", "hicolor"),
        "/var/lib/flatpak/exports/share/icons/hicolor",
    };

    /// <summary>PNG bytes for iconNameOrPath (a bare theme icon name, or an
    /// absolute path already ending in .png); empty when unresolved.</summary>
    internal static byte[] ResolvePng(string iconNameOrPath, IReadOnlyList<string> themeBases, IReadOnlyList<string> pixmapDirs)
    {
        if (iconNameOrPath.StartsWith('/'))
        {
            return iconNameOrPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(iconNameOrPath)
                ? File.ReadAllBytes(iconNameOrPath)
                : Array.Empty<byte>();
        }

        foreach (var basePath in themeBases)
        {
            foreach (var size in IconSizes)
            {
                var pngPath = Path.Combine(basePath, size, "apps", $"{iconNameOrPath}.png");
                if (File.Exists(pngPath))
                {
                    return File.ReadAllBytes(pngPath);
                }
            }
        }

        foreach (var dir in pixmapDirs)
        {
            var pngPath = Path.Combine(dir, $"{iconNameOrPath}.png");
            if (File.Exists(pngPath))
            {
                return File.ReadAllBytes(pngPath);
            }
        }

        return Array.Empty<byte>();
    }
}
