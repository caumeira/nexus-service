namespace Nexus.Service.Models.Cooling;

/// <summary>
/// Single source of truth for the <see cref="FanChannel.Kind"/> wire string —
/// what the channel physically is, independent of its control <see cref="FanModes"/>.
/// The panel's fan card picks the header icon from this (fan vs pump).
///
/// Values are case-sensitive and serialised as-is to the panel.
/// </summary>
public static class FanKinds
{
    /// <summary>A fan (the default for every motherboard / hub channel).</summary>
    public const string Fan = "Fan";

    /// <summary>An AIO pump head (Q-series today).</summary>
    public const string Pump = "Pump";
}
