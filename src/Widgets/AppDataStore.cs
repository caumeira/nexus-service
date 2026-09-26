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
/// <see cref="AppDataFile.Revision"/>, serialised per (appId, key) by an
/// in-process lock so two concurrent PUTs never interleave a read-modify-write.
/// </summary>
public sealed class AppDataStore
{
    public const int MaxDataBytes = 256 * 1024;
    public const int MaxKeysPerApp = 16;

    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);
    private readonly Func<string> _rootProvider;

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
    private static string LockKey(string appId, string key) => appId + "\u0000" + key;

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
        var serializedLength = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(data, PersistenceJsonContext.Default.JsonElement));
        if (serializedLength > MaxDataBytes)
        {
            return new PutResult { Outcome = PutOutcome.TooLarge };
        }

        var gate = _locks.GetOrAdd(LockKey(appId, key), static _ => new object());
        lock (gate)
        {
            var appDir = AppDir(appId);
            var path = FilePath(appId, key);
            var isNewKey = !File.Exists(path);
            var existing = TryRead(appId, key);

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
            return new PutResult { Outcome = PutOutcome.Ok, Revision = file.Revision, UpdatedAt = now, Data = data };
        }
    }

    /// <summary>Import path (profile archive restore, cloud pull): writes the document unconditionally at a revision strictly above whatever is currently stored, so a subscribed instance's stale-revision guard still lets the push through. Preserves any existing cloud sync state unless <paramref name="cloudState"/> overrides it.</summary>
    public AppDataFile Import(string appId, string key, JsonElement data, AppDataCloudState? cloudState = null)
    {
        var gate = _locks.GetOrAdd(LockKey(appId, key), static _ => new object());
        lock (gate)
        {
            var appDir = AppDir(appId);
            var path = FilePath(appId, key);
            var existing = TryRead(appId, key);
            var now = DateTimeOffset.UtcNow.ToString("o");
            var file = new AppDataFile
            {
                Revision = (existing?.Revision ?? 0) + 1,
                UpdatedAt = now,
                Data = data,
                Cloud = cloudState ?? existing?.Cloud,
            };
            WriteFile(appDir, path, file);
            return file;
        }
    }

    /// <summary>Persists only the cloud sync bookkeeping (revision/hash/syncedAt), leaving the document's own revision and data untouched. Used after a successful push so the next decision pass sees the document as clean.</summary>
    public void SetCloudState(string appId, string key, AppDataCloudState state)
    {
        var gate = _locks.GetOrAdd(LockKey(appId, key), static _ => new object());
        lock (gate)
        {
            var appDir = AppDir(appId);
            var path = FilePath(appId, key);
            var existing = TryRead(appId, key);
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
        var gate = _locks.GetOrAdd(LockKey(appId, key), static _ => new object());
        lock (gate)
        {
            try { File.Delete(FilePath(appId, key)); } catch { /* already gone */ }
        }
    }

    /// <summary>Every (appId, key) with a document on disk, for profile-archive export and the cloud sync pass. Skips an app id that fails <see cref="AppIds.IsValid"/> (defence in depth - directory names are never expected to be anything else).</summary>
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
