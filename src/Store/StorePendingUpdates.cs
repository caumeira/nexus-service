using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Widgets;

namespace Nexus.Service.Store;

/// <summary>
/// Updates waiting on the user's consent. Held in memory: the updater recomputes
/// them on every pass from the catalog and the installed manifests.
/// </summary>
public sealed class StorePendingUpdates
{
    private readonly ConcurrentDictionary<string, StorePendingUpdate> _items = new(StringComparer.Ordinal);

    public void Set(StorePendingUpdate update) => _items[update.AppId] = update;

    public bool Remove(string appId) => _items.TryRemove(appId, out _);

    public List<StorePendingUpdate> All() =>
        _items.Values.OrderBy(u => u.AppId, StringComparer.Ordinal).ToList();
}
