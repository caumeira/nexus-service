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
    // _order tracks recency: front (First) = most recently used, back
    // (Last) = least recently used, so eviction always removes Last.
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, (LinkedListNode<string> Node, byte[] Bytes)> _entries = new();

    public bool TryGet(string exePath, out byte[] bytes)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(exePath, out var entry))
            {
                bytes = entry.Bytes;
                _order.Remove(entry.Node);
                _order.AddFirst(entry.Node);
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
            if (_entries.TryGetValue(exePath, out var existing))
            {
                _order.Remove(existing.Node);
            }
            var node = _order.AddFirst(exePath);
            _entries[exePath] = (node, bytes);

            if (_entries.Count > MaxEntries)
            {
                var lru = _order.Last!;
                _order.RemoveLast();
                _entries.Remove(lru.Value);
            }
        }
    }
}
