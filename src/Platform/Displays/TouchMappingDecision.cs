using System;
using System.Collections.Generic;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Platform.Displays;

public enum TouchMappingOutcome
{
    NoPanel,
    NoDigitizer,
    AlreadyCorrect,
    NeedsRepair,
}

public sealed record TouchMappingRepairPlan(
    string DigitizerInterfacePath,
    string PanelDisplayId,
    string PanelMonitorInterfacePath);

/// <summary>
/// Pure decision matrix over a touch-mapping snapshot: whether a catalog
/// panel is attached, whether its digitizer is present, and whether the
/// current OS association already matches. A physical panel can expose
/// multiple digitizer HID collections, each with its own Digimon value, so
/// every catalog-matching digitizer is evaluated independently. No I/O -
/// TouchMappingGuard is the only caller that acts on the result.
/// </summary>
public static class TouchMappingDecision
{
    public static (TouchMappingOutcome Outcome, IReadOnlyList<TouchMappingRepairPlan> Plans) Decide(TouchMapSnapshot snapshot)
    {
        var plans = new List<TouchMappingRepairPlan>();
        var matchedPanel = false;
        var matchedDigitizer = false;

        foreach (var display in snapshot.Displays)
        {
            var entry = TouchPanelCatalog.MatchDisplay(display.MonitorInterfacePath);
            if (entry is null) continue;
            matchedPanel = true;

            foreach (var candidate in snapshot.Digitizers)
            {
                if (!TouchPanelCatalog.MatchesDigitizer(entry, candidate.InterfacePath)) continue;
                matchedDigitizer = true;

                if (string.Equals(candidate.AssociatedDisplayId, display.Id, StringComparison.Ordinal)) continue;

                plans.Add(new TouchMappingRepairPlan(candidate.InterfacePath, display.Id, display.MonitorInterfacePath));
            }
        }

        if (!matchedPanel) return (TouchMappingOutcome.NoPanel, plans);
        if (!matchedDigitizer) return (TouchMappingOutcome.NoDigitizer, plans);
        return (plans.Count > 0 ? TouchMappingOutcome.NeedsRepair : TouchMappingOutcome.AlreadyCorrect, plans);
    }
}
