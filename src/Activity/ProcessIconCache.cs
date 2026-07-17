using System.Collections.Generic;

namespace Nexus.Service.Activity;

/// <summary>
/// Small in-memory LRU cache of process-icon PNG bytes keyed by exe path.
/// Icons are immutable per path for the life of the service (a path whose
/// binary changes gets a fresh entry only after a restart), so this needs no
/// staleness check, unlike IconDiskCache's mtime-keyed shortcut entries. An
/// empty byte array is cached too - a negative result - so an unresolvable
/// exe is not re-extracted on every request.
/// </summary>
public sealed class ProcessIconCache
{
    private const int MaxEntries = 200;

    private readonly object _lock = new();
    // Insertion order doubles as recency order: a re-Set on an existing key
    // removes and re-adds it, so the front of the dictionary is always the
    // least recently used entry.
    private readonly Dictionary<string, byte[]> _entries = new();

    public bool TryGet(string exePath, out byte[] bytes)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(exePath, out var found))
            {
                bytes = found;
                _entries.Remove(exePath);
                _entries[exePath] = found;
                return true;
            }
        }
        bytes = System.Array.Empty<byte>();
        return false;
    }

    public void Set(string exePath, byte[] bytes)
    {
        lock (_lock)
        {
            _entries.Remove(exePath);
            _entries[exePath] = bytes;
            if (_entries.Count > MaxEntries)
            {
                using var e = _entries.Keys.GetEnumerator();
                e.MoveNext();
                _entries.Remove(e.Current);
            }
        }
    }
}
