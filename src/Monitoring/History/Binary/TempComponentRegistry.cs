using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Append-only id -&gt; ring-index registry for an entity that needs a Kind
/// alongside its Id/Name, unlike EntityRegistry's gpu/fan entries (each of
/// those already lives in a kind-specific directory, so no Kind column is
/// needed there). Two callers share this class: TempComponentRingStore
/// (storage/RAM entities, Kind "storage"/"ram") and TempBucketStore (bucket
/// keys spanning cpu/gpu/storage/ram in one registry). Structurally
/// identical to EntityRegistry - same ring-index-is-append-order convention,
/// same first-seen-name-wins policy, same crash-safety truncate-on-torn-tail
/// recovery - with one extra length-prefixed UTF8 field per record. Kept as
/// its own class rather than adding an optional Kind to EntityRegistry
/// itself, so gpu/fan's already-reviewed on-disk format and tests stay
/// untouched.
/// </summary>
internal sealed class TempComponentRegistry : IDisposable
{
    private readonly FileStream _file;
    private readonly int _capacity;
    private (string Id, string Kind, string Name)[] _entries = Array.Empty<(string, string, string)>();
    private readonly Dictionary<string, int> _indexById = new(StringComparer.Ordinal);

    private TempComponentRegistry(FileStream file, int capacity)
    {
        _file = file;
        _capacity = capacity;
        Load();
    }

    public static TempComponentRegistry Open(string path, int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "registry capacity must be positive.");
        }
        var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        return new TempComponentRegistry(file, capacity);
    }

    public int Count => Volatile.Read(ref _entries).Length;

    /// <summary>Every registered entity, in ring-index order (index ==
    /// position in this array) - a snapshot safe to enumerate even while
    /// RegisterOrGet is concurrently publishing a longer one.</summary>
    public IReadOnlyList<(string Id, string Kind, string Name)> Entries => Volatile.Read(ref _entries);

    /// <summary>Registers id/kind/name if unseen and returns its ring index,
    /// or returns the existing index if already registered (kind/name are
    /// then ignored, matching EntityRegistry's first-seen-wins policy).
    /// Returns null once the registry already holds <see cref="_capacity"/>
    /// distinct entities.</summary>
    public int? RegisterOrGet(string id, string kind, string name)
    {
        if (_indexById.TryGetValue(id, out var existing))
        {
            return existing;
        }
        var current = Volatile.Read(ref _entries);
        if (current.Length >= _capacity)
        {
            return null;
        }

        var index = current.Length;
        WriteRecord(id, kind, name);
        var next = new (string Id, string Kind, string Name)[current.Length + 1];
        Array.Copy(current, next, current.Length);
        next[index] = (id, kind, name);
        Volatile.Write(ref _entries, next);
        _indexById[id] = index;
        return index;
    }

    private void WriteRecord(string id, string kind, string name)
    {
        var idBytes = Encoding.UTF8.GetBytes(id);
        var kindBytes = Encoding.UTF8.GetBytes(kind);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(header[..4], idBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..8], kindBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], nameBytes.Length);

        _file.Seek(0, SeekOrigin.End);
        _file.Write(header);
        _file.Write(idBytes);
        _file.Write(kindBytes);
        _file.Write(nameBytes);
        _file.Flush(flushToDisk: true);
    }

    private void Load()
    {
        var loaded = new List<(string Id, string Kind, string Name)>();
        _file.Seek(0, SeekOrigin.Begin);
        Span<byte> header = stackalloc byte[12];
        while (true)
        {
            var recordStart = _file.Position;
            if (_file.Read(header) != 12)
            {
                _file.SetLength(recordStart);
                break;
            }

            var idLen = BinaryPrimitives.ReadInt32LittleEndian(header[..4]);
            var kindLen = BinaryPrimitives.ReadInt32LittleEndian(header[4..8]);
            var nameLen = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            if (idLen < 0 || kindLen < 0 || nameLen < 0)
            {
                _file.SetLength(recordStart);
                break;
            }

            var idBytes = new byte[idLen];
            var kindBytes = new byte[kindLen];
            var nameBytes = new byte[nameLen];
            if (_file.Read(idBytes) != idLen || _file.Read(kindBytes) != kindLen || _file.Read(nameBytes) != nameLen)
            {
                _file.SetLength(recordStart);
                break;
            }

            var id = Encoding.UTF8.GetString(idBytes);
            var kind = Encoding.UTF8.GetString(kindBytes);
            var name = Encoding.UTF8.GetString(nameBytes);
            _indexById[id] = loaded.Count;
            loaded.Add((id, kind, name));
        }
        _file.Seek(0, SeekOrigin.End);
        _entries = loaded.ToArray();
    }

    public void Dispose() => _file.Dispose();
}
