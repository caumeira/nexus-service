using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Global, append-only process-name -&gt; id dictionary shared by every app
/// metric kind (cpu/memory/gpu/vram) and every day segment - the binary
/// equivalent of SqliteMetricsHistoryStore's app_series table. Structurally
/// identical to EntityRegistry (append-order-is-index, Volatile-published
/// array, truncate-on-torn-tail recovery) with two differences: names dedupe
/// case-insensitively (matching app_series' COLLATE NOCASE, so "Chrome.exe"
/// and "chrome.exe" resolve to the same id with the first-seen casing kept),
/// and there is no capacity cap - a real system accumulates at most a few
/// thousand distinct process names over any realistic retention window,
/// trivial to hold in RAM, unlike gpu/fan/temperature-component counts which
/// are capped because they are indexed by a per-entity ring file.
///
/// AppUsageStore is the only writer (RegisterOrGet), matching the
/// single-writer assumption the whole binary store is built on; a concurrent
/// query resolves a name to an id via TryGetId, or an id back to its name via
/// Names, both reading only the Volatile-published array.
///
/// RegisterOrGet flushes every new record synchronously (see WriteRecord),
/// so a name is always durable before AppUsageStore.Append's day-segment
/// writes - which happen afterward, once per touched (kind, day), not once
/// per app - ever reference its id. A day segment can therefore never
/// persist a global id this dictionary does not already have durably, which
/// is what keeps GetName safe to call with any id a day segment yields.
/// </summary>
internal sealed class AppNameDictionary : IDisposable
{
    private readonly FileStream _file;
    private string[] _names = Array.Empty<string>();
    private readonly Dictionary<string, int> _idByName = new(StringComparer.OrdinalIgnoreCase);

    private AppNameDictionary(FileStream file)
    {
        _file = file;
        Load();
    }

    public static AppNameDictionary Open(string path)
    {
        var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        return new AppNameDictionary(file);
    }

    /// <summary>Every registered name, in id order (index == id) - a
    /// snapshot safe to enumerate even while RegisterOrGet is concurrently
    /// publishing a longer one.</summary>
    public IReadOnlyList<string> Names => Volatile.Read(ref _names);

    /// <summary>Registers name if unseen (case-insensitively) and returns its
    /// id, or returns the existing id - with the first-seen casing - if
    /// already registered.</summary>
    public int RegisterOrGet(string name)
    {
        if (_idByName.TryGetValue(name, out var existing))
        {
            return existing;
        }

        var current = Volatile.Read(ref _names);
        var id = current.Length;
        WriteRecord(name);
        var next = new string[current.Length + 1];
        Array.Copy(current, next, current.Length);
        next[id] = name;
        Volatile.Write(ref _names, next);
        _idByName[name] = id;
        return id;
    }

    /// <summary>The id for name (case-insensitive), or null if never
    /// registered - a pure lookup that never registers a new name, unlike
    /// RegisterOrGet. Scans the Volatile-published Names array rather than
    /// the private _idByName dictionary RegisterOrGet uses: _idByName has no
    /// Volatile discipline (only the single writer ever touches it), so a
    /// concurrent query reading it while RegisterOrGet inserts could see a
    /// torn dictionary. Names is bounded by how many distinct process names
    /// this box has ever seen (at most a few thousand - see the class doc),
    /// so the scan costs nothing worth avoiding.</summary>
    public int? TryGetId(string name)
    {
        var names = Volatile.Read(ref _names);
        for (var i = 0; i < names.Length; i++)
        {
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return null;
    }

    /// <summary>The name at id, as it was first seen.</summary>
    public string GetName(int id) => Volatile.Read(ref _names)[id];

    private void WriteRecord(string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, nameBytes.Length);

        _file.Seek(0, SeekOrigin.End);
        _file.Write(header);
        _file.Write(nameBytes);
        _file.Flush(flushToDisk: true);
    }

    private void Load()
    {
        var loaded = new List<string>();
        _file.Seek(0, SeekOrigin.Begin);
        Span<byte> header = stackalloc byte[4];
        while (true)
        {
            var recordStart = _file.Position;
            if (_file.Read(header) != 4)
            {
                _file.SetLength(recordStart);
                break;
            }

            var nameLen = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (nameLen < 0)
            {
                _file.SetLength(recordStart);
                break;
            }

            var nameBytes = new byte[nameLen];
            if (_file.Read(nameBytes) != nameLen)
            {
                _file.SetLength(recordStart);
                break;
            }

            var name = Encoding.UTF8.GetString(nameBytes);
            _idByName[name] = loaded.Count;
            loaded.Add(name);
        }
        _file.Seek(0, SeekOrigin.End);
        _names = loaded.ToArray();
    }

    public void Dispose() => _file.Dispose();
}
