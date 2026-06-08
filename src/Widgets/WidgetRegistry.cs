using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Serialization;

namespace Nexus.Service.Widgets;

/// <summary>
/// Discovers and caches installed widgets. Scanning is lazy on first read +
/// explicit <see cref="Refresh"/>; Phase 0 does not watch the filesystem.
/// </summary>
/// <remarks>
/// The registry is a singleton. Lookups are <see cref="StringComparer.Ordinal"/>
/// to match URL path semantics.
/// </remarks>
public sealed class WidgetRegistry
{
    private readonly Func<IReadOnlyList<WidgetInstallPaths.Root>> _rootsProvider;
    private readonly ConcurrentDictionary<string, WidgetEntry> _entries =
        new(StringComparer.Ordinal);
    private int _loaded;

    public WidgetRegistry() : this(() => WidgetInstallPaths.Enumerate()) { }

    /// <summary>
    /// Test seam: lets unit tests point the registry at a fixture directory.
    /// </summary>
    public WidgetRegistry(Func<IReadOnlyList<WidgetInstallPaths.Root>> rootsProvider)
    {
        _rootsProvider = rootsProvider;
    }

    public IReadOnlyCollection<WidgetEntry> All()
    {
        EnsureLoaded();
        return (IReadOnlyCollection<WidgetEntry>)_entries.Values;
    }

    public bool TryGet(string id, out WidgetEntry entry)
    {
        EnsureLoaded();
        return _entries.TryGetValue(id, out entry!);
    }

    public void Refresh()
    {
        _entries.Clear();
        Volatile.Write(ref _loaded, 0);
        EnsureLoaded();
    }

    private void EnsureLoaded()
    {
        if (Volatile.Read(ref _loaded) == 1) return;
        lock (_entries)
        {
            if (Volatile.Read(ref _loaded) == 1) return;
            foreach (var root in _rootsProvider())
            {
                LoadRoot(root);
            }
            Volatile.Write(ref _loaded, 1);
        }
    }

    private void LoadRoot(WidgetInstallPaths.Root root)
    {
        if (!Directory.Exists(root.Path)) return;
        foreach (var dir in Directory.EnumerateDirectories(root.Path))
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                using var stream = File.OpenRead(manifestPath);
                var manifest = JsonSerializer.Deserialize(stream, AppJsonContext.Default.WidgetManifest);
                if (manifest is null) continue;
                if (!WidgetIds.IsValid(manifest.Id)) continue;
                // Bundle id must match folder name; refusing the mismatch
                // prevents one bundle from impersonating another via folder
                // rename (and keeps URL → disk-path resolution unambiguous).
                var folderName = Path.GetFileName(dir);
                if (!string.Equals(folderName, manifest.Id, StringComparison.Ordinal)) continue;
                if (!string.Equals(manifest.Schema, "nexus.widget/2", StringComparison.Ordinal)) continue;
                // Every widget is an SDK (sandboxed remote-component) widget rendered
                // from widget.mjs in the sandboxed host. The legacy declarative
                // view-tree runtime has been removed; reject anything that isn't SDK.
                if (!string.Equals(manifest.Runtime, "sdk", StringComparison.Ordinal)) continue;
                if (!File.Exists(Path.Combine(dir, "widget.mjs"))) continue;
                // Apply default size if author omitted it.
                if (string.IsNullOrEmpty(manifest.DefaultSize) && manifest.Sizes.Count > 0)
                {
                    manifest.DefaultSize = manifest.Sizes[0];
                }

                var entry = new WidgetEntry
                {
                    Id = manifest.Id,
                    RootPath = dir,
                    Manifest = manifest,
                    Source = root.Source,
                };
                // First write wins. Roots are enumerated in shadowing order
                // (dev → user → bundled), so later roots cannot overwrite
                // an entry from a higher-precedence root.
                _entries.TryAdd(manifest.Id, entry);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Malformed or unreadable manifests are skipped rather than
                // failing service startup.
                Console.Error.WriteLine($"[widgets] skipping {dir}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
