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

    // Read lock-free from ~13 worker threads. Safe because every write replaces
    // each list reference (never mutates in place) and adds to the destination
    // list before removing from the other, so a reader mid-swap sees the id in
    // BOTH lists, never in neither. Disabled wins that transient, so a
    // just-enabled third-party device reads off for a cycle (fail-closed) and an
    // explicit choice is never readable as unset.
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

    /// <summary>True when the user has made no explicit on/off choice for this handler (in neither list).</summary>
    public bool IsUnset(string handlerId)
    {
        var devices = _store.Load().Devices;
        return !devices.NexusControlDisabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase)
            && !devices.NexusControlEnabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase);
    }

    public void SetEnabled(string handlerId, bool enabled) => _store.Update(s =>
    {
        var devices = s.Devices;
        if (enabled)
        {
            devices.NexusControlEnabled = WithId(devices.NexusControlEnabled, handlerId);
            devices.NexusControlDisabled = WithoutId(devices.NexusControlDisabled, handlerId);
        }
        else
        {
            devices.NexusControlDisabled = WithId(devices.NexusControlDisabled, handlerId);
            devices.NexusControlEnabled = WithoutId(devices.NexusControlEnabled, handlerId);
        }
    });

    /// <summary>
    /// Puts a never-set handler on the enabled list, for
    /// <see cref="DeviceAdoptionService"/>. The set-check and the write are one
    /// store mutation, so a concurrent <see cref="SetEnabled"/> cannot be read as
    /// unset and then overwritten - an explicit off stays off. Returns false when
    /// the user has already chosen on or off.
    /// </summary>
    public bool TryAdopt(string handlerId)
    {
        var adopted = false;
        _store.Update(s =>
        {
            var devices = s.Devices;
            if (devices.NexusControlDisabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase)
                || devices.NexusControlEnabled.Contains(handlerId, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
            devices.NexusControlEnabled = WithId(devices.NexusControlEnabled, handlerId);
            adopted = true;
        });
        return adopted;
    }

    private static List<string> WithId(List<string> ids, string handlerId) =>
        ids.Contains(handlerId, StringComparer.OrdinalIgnoreCase) ? ids : new List<string>(ids) { handlerId };

    private static List<string> WithoutId(List<string> ids, string handlerId) =>
        ids.Contains(handlerId, StringComparer.OrdinalIgnoreCase)
            ? ids.Where(id => !string.Equals(id, handlerId, StringComparison.OrdinalIgnoreCase)).ToList()
            : ids;
}
