using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexus.Service.Media;
using Nexus.Service.Models.Gallery;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Gallery;

/// <summary>
/// Per-system gallery source registry + item enumeration. Sources are
/// referenced files/folders on local disk plus uploaded images; every panel
/// surface of this PC draws from the same set. sources.json (plus the upload
/// files themselves) is the only persisted state — folders are rescanned on
/// each enumeration so external file changes show up without a watcher.
/// </summary>
public sealed class GalleryLibrary
{
    private const string SourcesFileName = "sources.json";
    public const int MaxItemsPerFolder = 500;
    public const long MaxUploadSize = 50 * 1024 * 1024;

    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".avif" };

    private static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly object _lock = new();
    private List<GallerySource>? _sources;
    private Dictionary<string, string> _itemPaths = new();
    // Bumped on every source mutation. EnumerateItems snapshots it and only
    // swaps its map in if no mutation happened mid-scan, so a slow scan can't
    // resurrect items from a source removed while it ran.
    private long _generation;
    private long _lastEnumerationTicks;

    public GalleryLibrary()
        : this(Path.Combine(MediaLibrary.ResolveDefaultRoot(), "Nexus", "gallery"))
    {
    }

    public GalleryLibrary(string rootDir)
    {
        RootDir = rootDir;
        Directory.CreateDirectory(rootDir);
    }

    public string RootDir { get; }
    public string ThumbsDir => Path.Combine(RootDir, "thumbs");
    public string UploadsDir => Path.Combine(RootDir, "uploads");

    public static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return Array.IndexOf(ImageExtensions, ext) >= 0;
    }

    /// <summary>
    /// Stable item id: first 16 hex chars of SHA-256 of the canonical path.
    /// Survives restarts and rescans so panel clients can cache by id. Windows
    /// paths hash case-folded so the same file reached via differently-cased
    /// sources collapses to one id.
    /// </summary>
    public static string ItemIdForPath(string canonicalPath)
    {
        var normalized = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? canonicalPath.ToLowerInvariant()
            : canonicalPath;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    public List<GallerySource> ListSources()
    {
        lock (_lock)
        {
            return new List<GallerySource>(LoadSources());
        }
    }

    public GallerySourceMutationResponse AddReference(string path, string kind)
    {
        if (kind != GallerySourceKinds.File && kind != GallerySourceKinds.Folder)
            return Fail("kind must be 'file' or 'folder'");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return Fail("path must be absolute");

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return Fail("invalid path");
        }

        if (kind == GallerySourceKinds.File)
        {
            if (!File.Exists(full)) return Fail("file not found");
            if (!IsImageFile(full)) return Fail("unsupported image format");
        }
        else if (!Directory.Exists(full))
        {
            return Fail("folder not found");
        }

        lock (_lock)
        {
            var sources = LoadSources();
            if (sources.Any(s => s.Kind == kind && string.Equals(s.Path, full, PathComparison)))
                return Fail("source already added");

            var source = NewSource(sources, kind, full, Path.GetFileName(full));
            sources.Add(source);
            SaveSources(sources);
            InvalidateItemsLocked();
            return new GallerySourceMutationResponse { Source = source };
        }
    }

    /// <summary>
    /// Take ownership of an uploaded temp file: move it under uploads/ and
    /// register it as an upload-kind source named after the original file.
    /// </summary>
    public GallerySourceMutationResponse AddUpload(string tempPath, string originalName)
    {
        if (!IsImageFile(originalName))
            return Fail("unsupported image format");

        lock (_lock)
        {
            var sources = LoadSources();
            var source = NewSource(sources, GallerySourceKinds.Upload, path: "", originalName);
            var dest = Path.Combine(UploadsDir, source.Id + Path.GetExtension(originalName).ToLowerInvariant());
            try
            {
                Directory.CreateDirectory(UploadsDir);
                File.Move(tempPath, dest, overwrite: true);
            }
            catch (Exception ex)
            {
                return Fail($"upload failed: {ex.Message}");
            }

            source.Path = dest;
            sources.Add(source);
            SaveSources(sources);
            InvalidateItemsLocked();
            return new GallerySourceMutationResponse { Source = source };
        }
    }

    /// <summary>Remove a source; upload-kind sources also lose their stored file.</summary>
    public bool RemoveSource(string id)
    {
        lock (_lock)
        {
            var sources = LoadSources();
            var source = sources.FirstOrDefault(s => s.Id == id);
            if (source is null)
                return false;

            sources.Remove(source);
            SaveSources(sources);
            InvalidateItemsLocked();

            if (source.Kind == GallerySourceKinds.Upload)
            {
                try
                {
                    File.Delete(source.Path);
                }
                catch { /* best-effort */ }
            }

            return true;
        }
    }

    /// <summary>
    /// Flatten all sources to the ordered item list (source order, then file
    /// name). Duplicate paths across sources collapse to the first occurrence.
    /// Refreshes the id → path map used by <see cref="ResolveItemPath"/>.
    /// </summary>
    public List<GalleryItem> EnumerateItems()
    {
        List<GallerySource> sources;
        long generation;
        lock (_lock)
        {
            sources = new List<GallerySource>(LoadSources());
            generation = _generation;
        }

        var items = new List<GalleryItem>();
        var paths = new Dictionary<string, string>();

        foreach (var source in sources)
        {
            if (source.Kind == GallerySourceKinds.Folder)
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(source.Path);
                }
                catch
                {
                    continue;
                }

                foreach (var file in files
                    .Where(IsImageFile)
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxItemsPerFolder))
                {
                    AddItem(file, source.Id);
                }
            }
            else if (File.Exists(source.Path) && IsImageFile(source.Path))
            {
                AddItem(source.Path, source.Id);
            }
        }

        lock (_lock)
        {
            // A mutation mid-scan means this map may contain items from a
            // removed source — drop it; the next enumeration rebuilds fresh.
            // The throttle timestamp only advances on a real swap, else a
            // discarded scan would arm it over an empty map and panel reads
            // would false-negative for the throttle window.
            if (generation == _generation)
            {
                _itemPaths = paths;
                _lastEnumerationTicks = Environment.TickCount64;
            }
        }

        return items;

        void AddItem(string file, string sourceId)
        {
            var full = Path.GetFullPath(file);
            var id = ItemIdForPath(full);
            if (paths.TryAdd(id, full))
            {
                items.Add(new GalleryItem { Id = id, Name = Path.GetFileName(full), SourceId = sourceId });
            }
        }
    }

    /// <summary>
    /// Resolve an item id to its absolute path. Only ids derived from the
    /// registered source set resolve — client-supplied paths never enter.
    /// </summary>
    public string? ResolveItemPath(string id)
    {
        lock (_lock)
        {
            if (_itemPaths.TryGetValue(id, out var cached))
                return cached;

            // Unknown-id requests are panel-reachable; without this throttle a
            // client spamming random ids forces a full disk rescan per request.
            if (Environment.TickCount64 - _lastEnumerationTicks < 2000)
                return null;
        }

        // Cold start or a freshly added file: rebuild the map once.
        EnumerateItems();
        lock (_lock)
        {
            return _itemPaths.TryGetValue(id, out var path) ? path : null;
        }
    }

    private GallerySource NewSource(List<GallerySource> existing, string kind, string path, string? name)
    {
        var baseName = string.IsNullOrEmpty(name) ? "source" : name;
        var id = MediaImporter.SanitizeId($"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-{baseName}");
        // Same-millisecond adds (multi-select) can collide; suffix until unique.
        var unique = id;
        for (var n = 2; existing.Any(s => s.Id == unique); n++)
        {
            unique = MediaImporter.SanitizeId($"{id}-{n}");
        }

        return new GallerySource
        {
            Id = unique,
            Kind = kind,
            Path = path,
            Name = baseName,
            AddedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    // Call under _lock after any source mutation: revokes every resolved item
    // id immediately (panel file/thumbnail reads must not outlive the source)
    // and marks in-flight enumerations stale.
    private void InvalidateItemsLocked()
    {
        _generation++;
        _itemPaths = new Dictionary<string, string>();
        _lastEnumerationTicks = 0;
    }

    private List<GallerySource> LoadSources()
    {
        if (_sources is not null)
            return _sources;

        var file = Path.Combine(RootDir, SourcesFileName);
        try
        {
            var parsed = JsonSerializer.Deserialize(File.ReadAllText(file), AppJsonContext.Default.GallerySourcesFile);
            _sources = parsed?.Sources
                .Where(s => MediaLibrary.IsValidId(s.Id) && !string.IsNullOrEmpty(s.Path) && !string.IsNullOrEmpty(s.Kind))
                .ToList() ?? new List<GallerySource>();
        }
        catch
        {
            // Missing or corrupt file: start empty rather than failing boot.
            _sources = new List<GallerySource>();
        }

        return _sources;
    }

    private void SaveSources(List<GallerySource> sources)
    {
        _sources = sources;
        Directory.CreateDirectory(RootDir);
        var json = JsonSerializer.Serialize(new GallerySourcesFile { Sources = sources }, AppJsonContext.Default.GallerySourcesFile);
        AtomicJsonFile.Write(Path.Combine(RootDir, SourcesFileName), json);
    }

    private static GallerySourceMutationResponse Fail(string msg) =>
        new() { Error = true, Msg = msg };
}
