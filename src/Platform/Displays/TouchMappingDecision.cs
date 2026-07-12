using System;
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
/// current OS association already matches. No I/O - TouchMappingGuard is the
/// only caller that acts on the result.
/// </summary>
public static class TouchMappingDecision
{
    public static (TouchMappingOutcome Outcome, TouchMappingRepairPlan? Plan) Decide(TouchMapSnapshot snapshot)
    {
        foreach (var display in snapshot.Displays)
        {
            var entry = TouchPanelCatalog.MatchDisplay(display.MonitorInterfacePath);
            if (entry is null) continue;

            TouchMapDigitizerInfo? digitizer = null;
            foreach (var candidate in snapshot.Digitizers)
            {
                if (TouchPanelCatalog.MatchesDigitizer(entry, candidate.InterfacePath))
                {
                    digitizer = candidate;
                    break;
                }
            }
            if (digitizer is null) return (TouchMappingOutcome.NoDigitizer, null);

            if (string.Equals(digitizer.AssociatedDisplayId, display.Id, StringComparison.Ordinal))
                return (TouchMappingOutcome.AlreadyCorrect, null);

            return (TouchMappingOutcome.NeedsRepair,
                new TouchMappingRepairPlan(digitizer.InterfacePath, display.Id, display.MonitorInterfacePath));
        }
        return (TouchMappingOutcome.NoPanel, null);
    }
}
