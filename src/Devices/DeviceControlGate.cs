using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Persistence;

namespace Nexus.Service.Devices;

/// <summary>
/// Per-handler Nexus Control on/off gate. Tri-state: a handler id in the disabled
/// list is off, in the enabled list is on, and in neither uses the brand default
/// (<see cref="DeviceControlPolicy.DefaultOn"/> - third-party hubs with a competing
/// app off, everything else on). Connection workers consult this before claiming a
/// port so a toggled-off device stays detectable (USB enumeration still sees it) but
/// unclaimed.
/// </summary>
public sealed class DeviceControlGate
{
    private readonly IConfigStore _store;

    public DeviceControlGate(IConfigStore store)
    {
        _store = store;
    }

    // Read lock-free from ~13 worker threads. Safe because SetEnabled always
    // replaces each list reference (never mutates in place). Reading the two
    // lists is not one atomic snapshot; a cross-swap transient reads one stale
    // list and resolves to the default for a cycle - fail-closed for a just-
    // enabled third-party device, one extra on-cycle for a just-disabled
    // default-on device. The worker poll loop tolerates either.
    public bool IsEnabled(string handlerId)
    {
        var devices = _store.Load().Devices;
        if (devices.NexusControlDisabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }
        if (devices.NexusControlEnabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }
        return DeviceControlPolicy.DefaultOn(handlerId);
    }

    public void SetEnabled(string handlerId, bool enabled) => _store.Update(s =>
    {
        var devices = s.Devices;
        if (enabled)
        {
            if (devices.NexusControlEnabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase)
                && !devices.NexusControlDisabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
            devices.NexusControlDisabled = devices.NexusControlDisabled
                .Where(id => !string.Equals(id, handlerId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (!devices.NexusControlEnabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase))
            {
                devices.NexusControlEnabled = new List<string>(devices.NexusControlEnabled) { handlerId };
            }
        }
        else
        {
            if (devices.NexusControlDisabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase)
                && !devices.NexusControlEnabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
            devices.NexusControlEnabled = devices.NexusControlEnabled
                .Where(id => !string.Equals(id, handlerId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (!devices.NexusControlDisabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase))
            {
                devices.NexusControlDisabled = new List<string>(devices.NexusControlDisabled) { handlerId };
            }
        }
    });
}
