using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Widgets;

/// <summary>
/// Generic per-(app,key) persistent JSON document store backing
/// <c>/apps-api/data/{appId}/{key}</c>. One file per document at
/// <c>&lt;NexusRoot&gt;/app-data/&lt;appId&gt;/&lt;key&gt;.json</c>, separate from
/// the app's install directory (install/update/uninstall wipes that; this
/// tree survives all three). Writes are compare-and-swap on
/// <see cref="AppDataFile.Revision"/>. Every public method validates its
/// appId/key with <see cref="AppIds.IsValid"/>/<see cref="AppDataKeys.IsValid"/>
/// regardless of whether the caller already did - callers that read an id
/// straight off the cloud (the account-wide app-data list) must never be able
/// to walk this store's paths, and this is the one place that can guarantee it.
/// A single lock per appId (not per key) covers every mutation to that app's
/// documents, so the per-app key-count cap can never be raced past by two
/// concurrent writes to two different new keys.
/// </summary>
public sealed class AppDataStore
{
    public const int MaxDataBytes = 256 * 1024;
    public const int MaxKeysPerApp = 16;

    private readonly ConcurrentDictionary<string, object> _appLocks = new(StringComparer.Ordinal);
    private readonly Func<string> _rootProvider;

    /// <summary>Fires after a mutation actually changes what a reader of this (appId, key) would see - a successful Put, Import, cloud-driven delete, or archive-and-remove. Never fires for SetCloudState (sync bookkeeping only, no visible document change). The one place every "broadcast on write" call site hooks into.</summary>
    public event Action<string, string>? DocumentChanged;

    public AppDataStore() : this(() => Path.Combine(NexusDataPaths.NexusRoot(), "app-data"))
    {
    }

    /// <summary>Test seam: an explicit root instead of the machine's real data directory.</summary>
    internal AppDataStore(Func<string> rootProvider)
    {
        _rootProvider = rootProvider;
    }

    private string RootDir => _rootProvider();
    private string AppDir(string appId) => Path.Combine(RootDir, appId);
    private string FilePath(string appId, string key) => Path.Combine(AppDir(appId), key + ".json");
    private string ArchiveDir(string appId, string archiveNamespace) => Path.Combine(RootDir, ".archive", SanitizeArchiveSegment(archiveNamespace), appId);

    private object AppLock(string appId) => _appLocks.GetOrAdd(appId, static _ => new object());

    private static void EnsureValid(string appId, string key)
    {
        if (!AppIds.IsValid(appId))
        {
            throw new ArgumentException("invalid app id", nameof(appId));
        }
        if (!AppDataKeys.IsValid(key))
        {
            throw new ArgumentException("invalid key", nameof(key));
        }
    }

    /// <summary>Keeps a foreign account id (from the cloud, not user input) out of the filesystem path unless it already looks like a plain id.</summary>
    private static string SanitizeArchiveSegment(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "unknown";
        }
        var chars = raw.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray();
        var cleaned = new string(chars);
        if (cleaned.Length == 0)
        {
            return "unknown";
        }
        return cleaned.Length > 64 ? cleaned[..64] : cleaned;
    }

    public enum PutOutcome { Ok, Conflict, TooLarge, TooManyKeys }

    public sealed class PutResult
    {
        public required PutOutcome Outcome { get; init; }
        public int Revision { get; init; }
        public string UpdatedAt { get; init; } = "";
        public JsonElement? Data { get; init; }
    }

    /// <summary>Reads the raw persisted file (including cloud sync metadata), or null when no document has ever been written for this app/key or the file is unreadable.</summary>
    public AppDataFile? TryRead(string appId, string key)
    {
        EnsureValid(appId, key);
        var path = FilePath(appId, key);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.AppDataFile);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Wire-shape read: revision 0 / null data when absent.</summary>
    public (int Revision, string UpdatedAt, JsonElement? Data) Get(string appId, string key)
    {
        var doc = TryRead(appId, key);
        return doc is null ? (0, "", null) : (doc.Revision, doc.UpdatedAt, doc.Data);
    }

    public PutResult Put(string appId, string key, int baseRevision, JsonElement data)
    {
        EnsureValid(appId, key);
        var serializedLength = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(data, PersistenceJsonContext.Default.JsonElement));
        if (serializedLength > MaxDataBytes)
        {
            return new PutResult { Outcome = PutOutcome.TooLarge };
        }

        lock (AppLock(appId))
        {
            var appDir = AppDir(appId);
            var path = FilePath(appId, key);
            var isNewKey = !File.Exists(path);
            var existing = TryReadUnlocked(path);

            if (isNewKey && CountKeys(appDir) >= MaxKeysPerApp)
            {
                return new PutResult { Outcome = PutOutcome.TooManyKeys };
            }

            var currentRevision = existing?.Revision ?? 0;
            if (baseRevision != currentRevision)
            {
                return new PutResult
                {
                    Outcome = PutOutcome.Conflict,
                    Revision = currentRevision,
                    UpdatedAt = existing?.UpdatedAt ?? "",
                    Data = existing?.Data,
                };
            }

            var now = DateTimeOffset.UtcNow.ToString("o");
            var file = new AppDataFile
            {
                Revision = currentRevision + 1,
                UpdatedAt = now,
                Data = data,
                Cloud = existing?.Cloud,
            };
            WriteFile(appDir, path, file);
            DocumentChanged?.Invoke(appId, key);
            return new PutResult { Outcome = PutOutcome.Ok, Revision = file.Revision, UpdatedAt = now, Data = data };
        }
    }

    /// <summary>
    /// Import path (profile archive restore, cloud sync). Unconditional
    /// (<paramref name="expectedRevision"/> null) writes a revision strictly
    /// above whatever is currently stored, for a restore that must always
    /// win. Conditional (<paramref name="expectedRevision"/> set) is a CAS on
    /// the revision the caller's decision was based on - returns null without
    /// writing anything if the document moved since, so a cloud-sync import
    /// racing a fresh local edit skips rather than clobbers it; the caller
    /// retries on its next pass. Preserves any existing cloud sync state
    /// unless <paramref name="cloudState"/> overrides it.
    /// </summary>
    public AppDataFile? Import(string appId, string key, JsonElement data, AppDataCloudState? cloudState = null, int? expectedRevision = null)
    {
        EnsureValid(appId, key);
        lock (AppLock(appId))
        {
            var appDir = AppDir(appId);
            var path = FilePath(appId, key);
            var existing = TryReadUnlocked(path);
            var currentRevision = existing?.Revision ?? 0;
            if (expectedRevision is { } expected && expected != currentRevision)
            {
                return null;
            }
            var now = DateTimeOffset.UtcNow.ToString("o");
            var file = new AppDataFile
            {
                Revision = currentRevision + 1,
                UpdatedAt = now,
                Data = data,
                Cloud = cloudState ?? existing?.Cloud,
            };
            WriteFile(appDir, path, file);
            DocumentChanged?.Invoke(appId, key);
            return file;
        }
    }

    /// <summary>Persists only the cloud sync bookkeeping (revision/hash/editedAt/syncedAt), leaving the document's own revision and data untouched. Used after a successful push so the next decision pass sees the document as clean. No DocumentChanged - nothing a reader of the document would see has moved.</summary>
    public void SetCloudState(string appId, string key, AppDataCloudState state)
    {
        EnsureValid(appId, key);
        lock (AppLock(appId))
        {
            var appDir = AppDir(appId);
            var path = FilePath(appId, key);
            var existing = TryReadUnlocked(path);
            if (existing is null)
            {
                return;
            }
            existing.Cloud = state;
            WriteFile(appDir, path, existing);
        }
    }

    public void Delete(string appId, string key)
    {
        EnsureValid(appId, key);
        lock (AppLock(appId))
        {
            var existed = File.Exists(FilePath(appId, key));
            try { File.Delete(FilePath(appId, key)); } catch { /* already gone */ }
            if (existed)
            {
                DocumentChanged?.Invoke(appId, key);
            }
        }
    }

    /// <summary>
    /// Moves the live document to <c>app-data/.archive/&lt;namespace&gt;/&lt;appId&gt;/&lt;key&gt;.json</c>
    /// and removes it from the live store, for a doc that belongs to a
    /// different (or no) cloud account than the one now signed in - see
    /// CloudProfileSyncService's account-scoping rules. A no-op (returns
    /// false) when there is nothing live to archive.
    /// </summary>
    public bool ArchiveAndRemove(string appId, string key, string archiveNamespace)
    {
        EnsureValid(appId, key);
        lock (AppLock(appId))
        {
            var path = FilePath(appId, key);
            if (!File.Exists(path))
            {
                return false;
            }
            var archiveDir = ArchiveDir(appId, archiveNamespace);
            Directory.CreateDirectory(archiveDir);
            var archivePath = Path.Combine(archiveDir, key + ".json");
            try
            {
                File.Copy(path, archivePath, overwrite: true);
                NexusDataPaths.CreateRestricted(archivePath);
            }
            catch (IOException)
            {
                return false;
            }
            try { File.Delete(path); } catch { /* best effort */ }
            DocumentChanged?.Invoke(appId, key);
            return true;
        }
    }

    /// <summary>Every (appId, key) with a document on disk, for profile-archive export and the cloud sync pass. The <c>.archive</c> namespace never shows up here - <see cref="AppIds.IsValid"/> rejects a leading dot.</summary>
    public IEnumerable<(string AppId, string Key)> EnumerateAll()
    {
        if (!Directory.Exists(RootDir))
        {
            yield break;
        }
        foreach (var appDir in Directory.EnumerateDirectories(RootDir))
        {
            var appId = Path.GetFileName(appDir);
            if (!AppIds.IsValid(appId))
            {
                continue;
            }
            foreach (var file in Directory.EnumerateFiles(appDir, "*.json"))
            {
                var key = Path.GetFileNameWithoutExtension(file);
                if (AppDataKeys.IsValid(key))
                {
                    yield return (appId, key);
                }
            }
        }
    }

    private static int CountKeys(string appDir)
    {
        if (!Directory.Exists(appDir))
        {
            return 0;
        }
        return Directory.EnumerateFiles(appDir, "*.json").Count();
    }

    private static AppDataFile? TryReadUnlocked(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.AppDataFile);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteFile(string appDir, string path, AppDataFile file)
    {
        Directory.CreateDirectory(appDir);
        var json = JsonSerializer.Serialize(file, PersistenceJsonContext.Default.AppDataFile);
        // Restrict the temp file BEFORE content lands in it, same ordering as
        // JsonConfigStore.WriteAtomic - a crash-stranded temp file must never
        // keep whatever the umask gave it.
        NexusDataPaths.CreateRestricted(AtomicJsonFile.TempPathFor(path));
        AtomicJsonFile.Write(path, json);
    }
}
