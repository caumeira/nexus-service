using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel;

/// <summary>
/// Resolves the console user's current macOS wallpaper as a browser-renderable
/// image. NSWorkspace's desktopImageURL and the System Events desktop API both
/// report a stale path once the wallpaper is an aerial, dynamic, or video
/// type, so the active choice is read from the wallpaper store: a file path
/// for picture wallpapers, and for aerials the asset id whose still ships in
/// the idle-assets snapshot directory.
///
/// WallpaperAgent renders a still of any wallpaper kind into its own container
/// under ~/Library/Containers, which would cover every type, but a signed app
/// reading another app's container hits the "data from other apps" gate, and
/// that gate BLOCKS the calling thread in open(2) rather than failing - it
/// wedged this endpoint for as long as the request was alive. Nothing here may
/// touch a container path, and the resolve stays behind a timeout so a future
/// gated path degrades to a 404 instead of hanging.
/// </summary>
internal static class MacDesktopWallpaperProvider
{
    private const string StoreRelative = "Library/Application Support/com.apple.wallpaper/Store/Index.plist";
    private const string AerialSnapshots = "/Library/Application Support/com.apple.idleassetsd/snapshots";

    // Ceiling for the served copy; a picture wallpaper can be far larger than
    // anything a panel background samples.
    private const int MaxServedEdge = 3840;

    // Covers the worst legitimate run (two plutil reads plus one image
    // conversion) with margin, so only a blocked syscall trips the latch.
    private static readonly TimeSpan ResolveBudget = TimeSpan.FromSeconds(20);
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".heic", ".tif", ".tiff", ".bmp" };

    // Latched when a resolve overruns its budget: the only known cause is a
    // permission gate that blocks in the kernel, which no retry clears.
    private static int _wedged;

    private static readonly object CacheLock = new();
    private static string? _cachedPath;
    private static DateTime _cachedStoreWrite;

    /// <summary>
    /// Resolved path for the current wallpaper, or null when it cannot be
    /// resolved. Cached against the store's write time so the plutil and sips
    /// work happens once per wallpaper change rather than once per panel load.
    /// </summary>
    public static string? TryResolve()
    {
        if (Volatile.Read(ref _wedged) != 0) return null;

        var storeWrite = File.GetLastWriteTimeUtc(StorePath());
        lock (CacheLock)
        {
            if (_cachedPath is not null && _cachedStoreWrite == storeWrite && File.Exists(_cachedPath))
                return _cachedPath;
        }

        var resolve = Task.Run(ResolveCore);
        try
        {
            if (!resolve.Wait(ResolveBudget))
            {
                Volatile.Write(ref _wedged, 1);
                ServiceLog.Warn("[wallpaper-mac] resolve exceeded its budget; wallpaper backgrounds disabled for this run");
                return null;
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[wallpaper-mac] resolve faulted: {ex.Message}");
            return null;
        }

        var path = resolve.Result;
        lock (CacheLock)
        {
            _cachedPath = path;
            _cachedStoreWrite = storeWrite;
        }
        return path;
    }

    internal static string StorePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StoreRelative);

    internal static string StoreDir() => Path.GetDirectoryName(StorePath())!;

    private static string? ResolveCore()
    {
        // Every failure here is a null, never a throw: this runs on a pool
        // thread whose exception would surface as a 500 instead of the route's
        // 404 contract, and the permission classes this file guards against
        // (UnauthorizedAccessException among them) are not otherwise caught.
        try
        {
            return ResolveUnguarded();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[wallpaper-mac] resolve failed: {ex.Message}");
            return null;
        }
    }

    private static string? ResolveUnguarded()
    {
        var xml = ShellExecutor.Run("/usr/bin/plutil", 3000, "-convert", "xml1", "-o", "-", StorePath());
        if (string.IsNullOrWhiteSpace(xml)) return null;

        XElement choice;
        try
        {
            var found = ActiveChoice(XDocument.Parse(xml));
            if (found is null) return null;
            choice = found;
        }
        catch (System.Xml.XmlException) { return null; }

        var picture = PicturePaths(choice).FirstOrDefault(File.Exists);
        if (picture is not null) return EnsureJpeg(picture);

        return AerialStill(choice) is { } still ? EnsureJpeg(still) : null;
    }

    /// <summary>
    /// The first choice of the desktop scope. The scope key is "Desktop" when
    /// the wallpaper and screen saver are set separately and "Linked" when one
    /// selection drives both; "Idle" is the screen saver alone and is skipped.
    /// </summary>
    internal static XElement? ActiveChoice(XDocument doc)
    {
        var root = doc.Root?.Elements("dict").FirstOrDefault();
        var scopes = DictValue(root, "AllSpacesAndDisplays") ?? DictValue(root, "SystemDefault");
        if (scopes is null) return null;
        var scope = DictValue(scopes, "Desktop") ?? DictValue(scopes, "Linked");
        var content = DictValue(scope, "Content");
        var choices = DictValue(content, "Choices");
        return choices?.Elements("dict").FirstOrDefault();
    }

    /// <summary>
    /// Every string in the choice that could name a picture on disk. The store
    /// carries picture paths under keys that have moved between macOS
    /// releases, so candidates are taken by shape (an image path or file URL)
    /// rather than by key name, and the caller keeps the first that exists.
    /// </summary>
    internal static IEnumerable<string> PicturePaths(XElement choice)
    {
        foreach (var value in choice.Descendants("string").Select(e => e.Value))
        {
            var path = value.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? TryLocalPath(value)
                : value;
            if (string.IsNullOrEmpty(path) || !path.StartsWith('/')) continue;
            if (!ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
            yield return path;
        }
    }

    private static string? TryLocalPath(string fileUrl)
        => Uri.TryCreate(fileUrl, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;

    /// <summary>
    /// The still shipped for an aerial choice. The choice's Configuration is a
    /// nested binary plist holding the asset id; the id names either a single
    /// aerial or a shuffling category, and the snapshot directory carries a
    /// preview for both spellings.
    /// </summary>
    private static string? AerialStill(XElement choice)
    {
        var configuration = DictValue(choice, "Configuration")?.Value;
        if (string.IsNullOrWhiteSpace(configuration)) return null;

        var temp = Path.Combine(Path.GetTempPath(), $"nexus-wp-{Guid.NewGuid():N}.plist");
        try
        {
            File.WriteAllBytes(temp, Convert.FromBase64String(configuration.Trim()));
            var inner = ShellExecutor.Run("/usr/bin/plutil", 3000, "-convert", "xml1", "-o", "-", temp);
            if (string.IsNullOrWhiteSpace(inner)) return null;
            var id = DictValue(XDocument.Parse(inner).Root?.Elements("dict").FirstOrDefault(), "assetID")?.Value;
            if (string.IsNullOrWhiteSpace(id)) return null;

            foreach (var prefix in new[] { "asset-preview-", "category-preview-" })
            {
                var candidate = Path.Combine(AerialSnapshots, $"{prefix}{id}.jpg");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
        catch (Exception ex) when (ex is FormatException or IOException or System.Xml.XmlException) { return null; }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }

    /// <summary>Value element following <paramref name="key"/> in a plist
    /// dict, which stores keys and values as sibling pairs.</summary>
    internal static XElement? DictValue(XElement? dict, string key)
    {
        if (dict is null) return null;
        var match = dict.Elements("key").FirstOrDefault(e => e.Value == key);
        return match?.ElementsAfterSelf().FirstOrDefault();
    }

    /// <summary>
    /// Converts a still the browser cannot decode (a picture wallpaper is
    /// commonly HEIC) into a cached JPEG, keyed by the source's write time so
    /// a wallpaper change misses the cache.
    /// </summary>
    private static string? EnsureJpeg(string source)
    {
        var ext = Path.GetExtension(source);
        if (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        var dir = Path.Combine(NexusDataPaths.NexusRoot(), "cache", "wallpaper");
        var stamp = File.GetLastWriteTimeUtc(source).Ticks;
        var dest = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(source)}-{stamp}.jpg");
        if (File.Exists(dest)) return dest;

        try { Directory.CreateDirectory(dir); }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[wallpaper-mac] cache dir failed: {ex.Message}");
            return null;
        }

        // Convert to a per-call temp name and move into place: concurrent panel
        // loads resolve the same source, and a second sips writing the
        // destination directly would serve a torn file to the first.
        var staged = Path.Combine(dir, $"{Guid.NewGuid():N}.jpg.tmp");
        var exit = ShellExecutor.RunExit("/usr/bin/sips", 8000,
            "-s", "format", "jpeg", "-Z", MaxServedEdge.ToString(),
            source, "--out", staged);
        if (exit != 0 || !File.Exists(staged))
        {
            ServiceLog.Warn($"[wallpaper-mac] sips exit={exit} for {Path.GetFileName(source)}");
            try { File.Delete(staged); } catch (IOException) { }
            return null;
        }
        try { File.Move(staged, dest, overwrite: true); }
        catch (IOException ex)
        {
            ServiceLog.Warn($"[wallpaper-mac] cache move failed: {ex.Message}");
            try { File.Delete(staged); } catch (IOException) { }
            return File.Exists(dest) ? dest : null;
        }
        PruneExcept(dir, dest);
        return dest;
    }

    private static void PruneExcept(string dir, string keep)
    {
        try
        {
            // Converted copies only. A concurrent resolve's staged temp shares
            // this directory, and deleting it would fail that resolve at its
            // own move.
            foreach (var file in Directory.EnumerateFiles(dir, "*.jpg"))
            {
                if (string.Equals(file, keep, StringComparison.Ordinal)) continue;
                if (!Path.GetExtension(file).Equals(".jpg", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(file); } catch (IOException) { }
            }
        }
        catch (DirectoryNotFoundException) { }
    }
}
