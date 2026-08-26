using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Cooling;

/// <summary>
/// Persists the "controlled" flag for fan channel ids, the cooling twin of
/// <see cref="Nexus.Service.Lighting.LightingControlledState"/>. No provider owns
/// it: uncontrolled is pure persisted state the write chokepoint and the preset
/// apply consult, so the mutation lives here.
///
/// Distinct from BIOS Control, which is the absence of a curve attachment and a
/// manual duty - anything that assigns fans (a preset apply, a FanControl
/// import) reclaims such a channel on its next run. This flag survives both,
/// and gates every duty write at
/// <see cref="CompositeFanControlProvider"/>. It does NOT stop a user from
/// wiring the channel to a curve by hand through POST /cooling/curves; the
/// card offers no such control while the marker is set, and the gate keeps the
/// attachment inert if one is made another way.
/// </summary>
public static class FanControlledState
{
    public static bool IsControlled(string id, NexusSettings settings)
    {
        // One read of the reference: SetControlled swaps it rather than mutating,
        // so two reads could straddle a swap - which is exactly what the swap
        // discipline exists to prevent.
        var uncontrolled = settings.Cooling.UncontrolledFanChannels;
        return !uncontrolled.Contains(id);
    }

    /// <summary>
    /// Write id's controlled state, replacing the list reference rather than
    /// mutating in place so the curve worker reading it lock-free never
    /// observes a torn state.
    /// </summary>
    public static void SetControlled(string id, bool controlled, IConfigStore store)
    {
        store.Update(s =>
        {
            var current = s.Cooling.UncontrolledFanChannels;
            if (controlled)
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
                s.Cooling.UncontrolledFanChannels = next;
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
                s.Cooling.UncontrolledFanChannels = next;
            }
        });
    }
}
