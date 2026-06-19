namespace Nexus.Service.Cooling;

/// <summary>
/// Shared plausibility gate for temperature sources across every platform's
/// fan-control provider (hwmon on Linux, LibreHardwareMonitor on Windows, SMC
/// on macOS). A disconnected / disabled hardware temp channel reports a junk
/// sentinel - an unconnected ITE SuperIO header reads -55°C on Linux, an
/// unpopulated DIMM SPD temp reads 0 or ~0.25°C via LHM on Windows. Those must
/// not surface as curve sources, or a new/preset curve can default its input to
/// a fake reading. Keeping the rule here means the cooling source list behaves
/// identically on every platform, not just wherever the cut was first added.
/// </summary>
internal static class TemperatureSourceFilter
{
    /// <summary>
    /// Below this (°C) a reading is treated as a disconnected/disabled channel,
    /// not a real component. No CPU/GPU/VRM/board/storage sensor in a powered
    /// PC sits at or under 1°C; the known sentinels (-55, 0, 0.25) all fall here.
    /// </summary>
    internal const float MinPlausibleCelsius = 1f;

    /// <summary>True when a temperature reading looks like a real sensor.</summary>
    internal static bool IsPlausible(float celsius) => celsius > MinPlausibleCelsius;
}
