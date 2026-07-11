namespace Nexus.Service.Sensors;

/// <summary>
/// Guards LHM-sourced float readings before they enter a wire DTO.
/// LibreHardwareMonitor sensors (SPD/DIMM reads in particular) occasionally
/// report NaN or +-Infinity; System.Text.Json throws on any non-finite float,
/// which fails the whole envelope build for every subscriber, not just the
/// offending sensor. Kept in its own file with no LibreHardwareMonitor types
/// so it compiles and tests on every platform, same reason as
/// <see cref="LhmComponentIdentifiers"/> and <see cref="CpuClockAggregates"/>.
/// </summary>
internal static class SensorValueSanitizer
{
    internal static float Sanitize(float value) => float.IsFinite(value) ? value : 0f;
}
