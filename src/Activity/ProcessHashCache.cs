using System.Collections.Generic;

namespace Nexus.Service.Activity;

/// <summary>
/// Small in-memory LRU cache of a computed sha256 hex digest keyed by
/// (path, mtime). A file's content only changes on a rewrite, which bumps
/// its last-write time, so the mtime is the staleness check - no separate
/// invalidation needed. Same LRU shape as ProcessIconCache.
/// </summary>
public sealed class ProcessHashCache
{
    private const int MaxEntries = 100;

    private readonly object _lock = new();
    private readonly LinkedList<(string Path, long MtimeTicks)> _order = new();
    private readonly Dictionary<(string Path, long MtimeTicks), (LinkedListNode<(string, long)> Node, string Hash)> _entries = new();

    public bool TryGet(string path, long mtimeTicks, out string hash)
    {
        var key = (path, mtimeTicks);
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                hash = entry.Hash;
                _order.Remove(entry.Node);
                _order.AddFirst(entry.Node);
                return true;
            }
        }
        hash = "";
        return false;
    }

    public void Set(string path, long mtimeTicks, string hash)
    {
        var key = (path, mtimeTicks);
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                _order.Remove(existing.Node);
            }
            var node = _order.AddFirst(key);
            _entries[key] = (node, hash);

            if (_entries.Count > MaxEntries)
            {
                var lru = _order.Last!;
                _order.RemoveLast();
                _entries.Remove(lru.Value);
            }
        }
    }
}
