using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Persists the "driven" flag for lighting device ids. No provider owns this
/// action - undriven is pure persisted state every frame writer consults, so
/// the mutation lives here rather than on <see cref="Devices.ILightingDeviceProvider"/>.
/// </summary>
public static class LightingDrivenState
{
    /// <summary>
    /// Write id's driven state, replacing the list reference rather than
    /// mutating in place so the 30fps frame writers reading it lock-free
    /// never observe a torn state.
    /// </summary>
    public static void SetDriven(string id, bool driven, IConfigStore store)
    {
        store.Update(s =>
        {
            var current = s.Devices.UndrivenLightingDevices;
            if (driven)
            {
                if (!current.Contains(id))
                {
                    return;
                }
                var next = new List<string>(current.Count);
                foreach (var x in current)
                {
                    if (x != id)
                    {
                        next.Add(x);
                    }
                }
                s.Devices.UndrivenLightingDevices = next;
            }
            else
            {
                if (current.Contains(id))
                {
                    return;
                }
                var next = new List<string>(current.Count + 1);
                next.AddRange(current);
                next.Add(id);
                s.Devices.UndrivenLightingDevices = next;
            }
        });
    }
}
