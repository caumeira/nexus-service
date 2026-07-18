using System.Collections.Generic;

namespace Nexus.Service.Models.Cooling;

/// <summary>
/// Single source of truth for the <see cref="FanChannel.Role"/> wire string -
/// which physical component (CPU/GPU) a fan belongs to for display and
/// monitoring-grouping purposes only. Never read by fan control, locking, or
/// preset logic.
///
/// Values are case-sensitive and serialised as-is to the panel. Named
/// distinctly from <see cref="Nexus.Service.Persistence.CoolingSettings.FanRoles"/>
/// (the settings dict) so the two never read as the same symbol.
/// </summary>
public static class FanRoleKind
{
    /// <summary>No role assigned (the default).</summary>
    public const string None = "none";

    /// <summary>Fan belongs to the CPU cooler.</summary>
    public const string Cpu = "cpu";

    /// <summary>Fan belongs to the GPU cooler.</summary>
    public const string Gpu = "gpu";

    public static readonly HashSet<string> Valid = new() { None, Cpu, Gpu };
}
