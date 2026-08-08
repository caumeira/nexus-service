using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Linux;

namespace Nexus.Service.Panel;

/// <summary>
/// Resolves the session user's current wallpaper on Linux: GNOME via
/// gsettings, KDE (Plasma) via its own config file. gsettings runs as the
/// session user through LinuxSession.WrapSpawnAsSessionUser - a root
/// daemon's bare gsettings call reads root's own empty dconf database, not
/// the logged-in user's. The resolved path is user-controlled, so it is
/// validated against IsSafeToServe before the (root-owned) route reads it.
/// </summary>
internal static class LinuxDesktopWallpaperProvider
{
    private const int GsettingsTimeoutMs = 3000;
    private const int StatTimeoutMs = 2000;

    // Recognized image extensions, mirroring PanelRoutes.WallpaperContentTypeFor.
    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif",
    };

    private static readonly string[] TrustedSystemWallpaperDirs = { "/usr/share/backgrounds", "/usr/share/wallpapers" };

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    private static readonly object CacheLock = new();
    private static string? _cachedPath;
    private static DateTime _cachedAtUtc = DateTime.MinValue;

    public static string? TryResolve()
    {
        lock (CacheLock)
        {
            if (DateTime.UtcNow - _cachedAtUtc < CacheTtl)
                return _cachedPath;
        }

        // A short TTL avoids paying gsettings' own timeout on every request
        // when the D-Bus session is degraded or unavailable; InvalidateCache
        // clears this early once a real change is detected.
        var resolved = ResolveUncached();

        lock (CacheLock)
        {
            _cachedPath = resolved;
            _cachedAtUtc = DateTime.UtcNow;
        }
        return resolved;
    }

    /// <summary>Forces the next TryResolve to re-run rather than serve the
    /// cached value - called from the wallpaper-change debounce so a client
    /// refetch triggered by that same signal never hits a stale cache
    /// entry.</summary>
    internal static void InvalidateCache()
    {
        lock (CacheLock)
        {
            _cachedAtUtc = DateTime.MinValue;
        }
    }

    private static string? ResolveUncached()
    {
        var path = TryResolveGnome() ?? TryResolveKde();
        return path is null ? null : ResolveSafePath(path);
    }

    /// <summary>Canonicalizes path (see ResolveRealPath) and returns that
    /// resolved real path only if it passes every safety gate - never the
    /// original path, so a traversal segment or symlink the gates evaluated
    /// away can't reach the caller.</summary>
    internal static string? ResolveSafePath(string path)
    {
        var real = ResolveRealPath(path);
        return real is not null && PassesSafetyGates(real) ? real : null;
    }

    /// <summary>Resolves path to its final real location: lexical
    /// normalization (.., .) via Path.GetFullPath, then the full symlink
    /// chain. Null if the path, or its chain's final target, does not
    /// exist.</summary>
    internal static string? ResolveRealPath(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        if (!File.Exists(full))
            return null;

        try
        {
            var target = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
                return full;
            return target.Exists ? target.FullName : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Gates what a root-owned route may read on the session user's
    /// say-so (gsettings picture-uri / KDE Image=): a recognized image
    /// extension, and either a known system wallpaper directory, a
    /// world-readable file, or (when running as a root daemon) a file owned
    /// by the adopted session user. realPath must already be the resolved
    /// real path (see ResolveRealPath) - this performs no traversal or
    /// symlink resolution of its own, so gating an un-resolved path would let
    /// a symlink or ".." segment slip past a check that inspects a different
    /// file than the one actually served.</summary>
    internal static bool PassesSafetyGates(string realPath)
    {
        if (!AllowedImageExtensions.Contains(Path.GetExtension(realPath)))
            return false;

        foreach (var dir in TrustedSystemWallpaperDirs)
        {
            if (realPath.StartsWith(dir + "/", StringComparison.Ordinal))
                return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                if ((File.GetUnixFileMode(realPath) & UnixFileMode.OtherRead) != 0)
                    return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Not an escalated root daemon - this process already runs as the
        // session user, so anything it resolved it could already read.
        if (LinuxSession.SessionUid is not uint sessionUid)
            return true;

        // -L: realPath is already the resolved target (see ResolveRealPath),
        // so this is defensive, not load-bearing.
        var owner = ShellExecutor.Run("stat", StatTimeoutMs, "-L", "-c", "%u", realPath);
        return uint.TryParse(owner.Trim(), out var ownerUid) && ownerUid == sessionUid;
    }

    /// <summary>Directory whose writes mean the GNOME wallpaper may have
    /// changed: the dconf user database, a single small file. KDE writes its
    /// wallpaper choice into the broader ~/.config tree, too noisy to watch
    /// cheaply, so a KDE change is only picked up on the next request.</summary>
    internal static string? WatchDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, ".config", "dconf");
        return Directory.Exists(dir) ? dir : null;
    }

    internal static bool IsServedFile(string? name)
        => string.Equals(name is null ? "" : Path.GetFileName(name), "user", StringComparison.Ordinal);

    private static string? TryResolveGnome()
    {
        var scheme = RunGsettings("org.gnome.desktop.interface", "color-scheme");
        var (primary, fallback) = ResolveKeyOrder(scheme);

        var uri = RunGsettings("org.gnome.desktop.background", primary)
            ?? RunGsettings("org.gnome.desktop.background", fallback);
        var path = uri is null ? null : FileUriToPath(uri);
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>Picture key to try first for the interface's current color
    /// scheme, and the other key as a fallback when the preferred one is
    /// unset.</summary>
    internal static (string Primary, string Fallback) ResolveKeyOrder(string? colorScheme)
    {
        var dark = colorScheme is not null && colorScheme.Contains("prefer-dark", StringComparison.Ordinal);
        return dark ? ("picture-uri-dark", "picture-uri") : ("picture-uri", "picture-uri-dark");
    }

    private static string? RunGsettings(string schema, string key)
    {
        var (file, args) = LinuxSession.WrapSpawnAsSessionUser("gsettings", new List<string> { "get", schema, key });
        var output = ShellExecutor.Run(file, GsettingsTimeoutMs, args.ToArray());
        return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
    }

    /// <summary>Un-quotes a gsettings string value ('file:///...') and
    /// resolves a file:// URI to a local path. Null for anything else,
    /// including the empty-string value gsettings reports when no wallpaper
    /// is set.</summary>
    internal static string? FileUriToPath(string raw)
    {
        var value = raw.Trim();
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            value = value[1..^1];

        if (value.Length == 0)
            return null;

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;
    }

    private static string? TryResolveKde()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configPath = Path.Combine(home, ".config", "plasma-org.kde.plasma.desktop-appletsrc");
        if (!File.Exists(configPath))
            return null;

        try
        {
            foreach (var value in ParseKdeImageValues(File.ReadLines(configPath)))
            {
                var path = FileUriToPath(value) ?? value;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return path;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return null;
    }

    /// <summary>Every Image= value in a plasma-org.kde.plasma.desktop-appletsrc
    /// file, in file order. Each containment (screen/activity) carries its
    /// own line, so the caller keeps the first that resolves to a real file.</summary>
    internal static IEnumerable<string> ParseKdeImageValues(IEnumerable<string> lines)
    {
        const string prefix = "Image=";
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                yield return line[prefix.Length..].Trim();
        }
    }
}
