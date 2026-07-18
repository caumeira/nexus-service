using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Append-only id -> ring-index registry shared by every entity kind (gpu,
/// fan; a temperature-component kind follows in a later phase) - the binary
/// equivalent of SqliteMetricsHistoryStore's gpu_series/fan_series tables.
/// Ring index is implicit: the Nth record ever appended is index N-1, so
/// GpuRingStore/FanRingStore can address their per-entity RingFile pair by
/// plain array position with no separate index column to keep in sync.
///
/// Unlike gpu_series/fan_series (whose name column updates on every
/// re-registration), a name is fixed at first registration and never
/// revised - first-seen wins, the same convention MetricsHistory's app
/// dictionary already uses for a process name observed with differing case.
/// No pinned test exercises a hardware id renaming itself (it doesn't, in
/// practice), so this only differs from SQLite in a scenario nothing here
/// relies on.
///
/// Record format is a flat, unindexed append log (idLen:i32 | id:utf8 |
/// nameLen:i32 | name:utf8), read entirely into RAM on open - registration is
/// rare (once per newly-seen entity, not once per tick) and the whole
/// registry lives well under a filesystem block, so plain FileStream I/O
/// costs nothing worth avoiding versus a RingFile-style mmap. Crash-safety is
/// weaker than RingFile's by design: Load stops at, and truncates away, the
/// first record that fails to read in full, so a crash mid-append loses at
/// most the one pending registration and never misreads a torn tail as a
/// valid (and wrongly-indexed) entity.
///
/// Entries publish through a single array field swapped via Volatile.Write,
/// never mutated in place - the same "swap whole arrays" discipline
/// LightingEngine's DeviceFrame[] uses. RegisterOrGet is the only writer
/// (the single-writer assumption the whole binary store is built on); a
/// concurrent HTTP route thread reads Count/Entries lock-free through
/// Volatile.Read, so it always sees a fully-formed array (never one whose
/// Length has grown ahead of the entries actually copied into it) - a plain
/// List&lt;T&gt; growing under a concurrent indexed read cannot make the same
/// guarantee, since a resize can be observed mid-copy.
/// </summary>
internal sealed class EntityRegistry : IDisposable
{
    private readonly FileStream _file;
    private readonly int _capacity;
    private (string Id, string Name)[] _entries = Array.Empty<(string, string)>();
    private readonly Dictionary<string, int> _indexById = new(StringComparer.Ordinal);

    private EntityRegistry(FileStream file, int capacity)
    {
        _file = file;
        _capacity = capacity;
        Load();
    }

    public static EntityRegistry Open(string path, int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "registry capacity must be positive.");
        }
        var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        return new EntityRegistry(file, capacity);
    }

    public int Count => Volatile.Read(ref _entries).Length;

    /// <summary>Every registered entity, in ring-index order (index ==
    /// position in this array) - a snapshot safe to enumerate even while
    /// RegisterOrGet is concurrently publishing a longer one.</summary>
    public IReadOnlyList<(string Id, string Name)> Entries => Volatile.Read(ref _entries);

    /// <summary>Ring index for <paramref name="id"/> if already registered, or
    /// null otherwise - a read-only lookup safe to call concurrently with
    /// RegisterOrGet. Scans the same Volatile-published array Entries/Count
    /// already expose rather than the private id-&gt;index dictionary
    /// RegisterOrGet itself uses (that dictionary is mutated with no Volatile
    /// discipline, since only the single writer ever touches it), so a
    /// concurrent registration is either fully visible here or not observed
    /// yet, never partially. Entity counts this registry ever holds are
    /// capacity-bounded and small, so the linear scan costs nothing worth
    /// avoiding.</summary>
    public int? TryGetIndex(string id)
    {
        var entries = Volatile.Read(ref _entries);
        for (var i = 0; i < entries.Length; i++)
        {
            if (entries[i].Id == id)
            {
                return i;
            }
        }
        return null;
    }

    /// <summary>Registers <paramref name="id"/>/<paramref name="name"/> if
    /// unseen and returns its ring index, or returns the existing index if
    /// already registered. Returns null - registering nothing - once the
    /// registry already holds <see cref="_capacity"/> distinct entities: a
    /// Capacity-plus-first entity has no ring to write to, matching Phase 1's
    /// own "silently drop what this phase does not persist" precedent rather
    /// than throwing or evicting an existing entity.</summary>
    public int? RegisterOrGet(string id, string name)
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
        WriteRecord(id, name);
        var next = new (string Id, string Name)[current.Length + 1];
        Array.Copy(current, next, current.Length);
        next[index] = (id, name);
        Volatile.Write(ref _entries, next);
        _indexById[id] = index;
        return index;
    }

    private void WriteRecord(string id, string name)
    {
        var idBytes = Encoding.UTF8.GetBytes(id);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header[..4], idBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], nameBytes.Length);

        _file.Seek(0, SeekOrigin.End);
        _file.Write(header);
        _file.Write(idBytes);
        _file.Write(nameBytes);
        _file.Flush(flushToDisk: true);
    }

    private void Load()
    {
        var loaded = new List<(string Id, string Name)>();
        _file.Seek(0, SeekOrigin.Begin);
        Span<byte> header = stackalloc byte[8];
        while (true)
        {
            var recordStart = _file.Position;
            if (_file.Read(header) != 8)
            {
                _file.SetLength(recordStart);
                break;
            }

            var idLen = BinaryPrimitives.ReadInt32LittleEndian(header[..4]);
            var nameLen = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            if (idLen < 0 || nameLen < 0)
            {
                _file.SetLength(recordStart);
                break;
            }

            var idBytes = new byte[idLen];
            var nameBytes = new byte[nameLen];
            if (_file.Read(idBytes) != idLen || _file.Read(nameBytes) != nameLen)
            {
                _file.SetLength(recordStart);
                break;
            }

            var id = Encoding.UTF8.GetString(idBytes);
            var name = Encoding.UTF8.GetString(nameBytes);
            _indexById[id] = loaded.Count;
            loaded.Add((id, name));
        }
        _file.Seek(0, SeekOrigin.End);
        _entries = loaded.ToArray();
    }

    public void Dispose() => _file.Dispose();
}
