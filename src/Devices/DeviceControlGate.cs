using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Persistence;

namespace Nexus.Service.Devices;

/// <summary>
/// Per-handler Nexus Control on/off gate. A handler id absent from the
/// persisted disabled list is controlled (on) by default; connection
/// workers consult this before claiming a port so a toggled-off device
/// stays detectable (USB enumeration still sees it) but unclaimed.
/// </summary>
public sealed class DeviceControlGate
{
    private readonly IConfigStore _store;

    public DeviceControlGate(IConfigStore store)
    {
        _store = store;
    }

    // Read lock-free from ~13 worker threads. Safe only because SetEnabled
    // always replaces the list reference (never mutates in place), so a reader
    // enumerates a stable snapshot. Keep that invariant if you change SetEnabled.
    public bool IsEnabled(string handlerId)
        => !_store.Load().Devices.NexusControlDisabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase);

    public void SetEnabled(string handlerId, bool enabled) => _store.Update(s =>
    {
        var current = s.Devices.NexusControlDisabled;
        var isDisabled = current.Contains(handlerId, StringComparer.OrdinalIgnoreCase);
        if (enabled)
        {
            if (!isDisabled) return;
            s.Devices.NexusControlDisabled = current.Where(id => !string.Equals(id, handlerId, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        else
        {
            if (isDisabled) return;
            var next = new List<string>(current) { handlerId };
            s.Devices.NexusControlDisabled = next;
        }
    });
}
