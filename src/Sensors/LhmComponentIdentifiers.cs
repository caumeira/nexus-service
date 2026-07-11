namespace Nexus.Service.Sensors;

/// <summary>
/// Identifier rules for LHM hardware nodes that need filtering or a distinct
/// wire id beyond a plain HardwareType match. Kept in its own file with no
/// LibreHardwareMonitor types so it compiles and tests on every platform,
/// including where LibreHardwareSensorProvider itself does not (same reason
/// CpuClockAggregates is split out).
/// </summary>
internal static class LhmComponentIdentifiers
{
    // LHM's MemoryGroup adds one node per DIMM at "/memory/dimm/{index}" (0.9.6),
    // alongside the "/ram" (physical total) and "/vram" (pagefile) nodes that
    // also report HardwareType.Memory. Matched by prefix rather than the full
    // "/memory/dimm/" path so a future LHM version that drops the trailing
    // slash still counts as a DIMM; "/ram" and "/vram" don't share this prefix.
    private const string DimmIdentifierPrefix = "/memory/dimm";

    // Marks an LHM physical-drive SMART component so consumers that only want
    // the DriveInfo logical-volume subset (the Tryx overlay) can filter it
    // back out of ISensorProvider.GetStorageComponents.
    private const string SmartStorageIdPrefix = "smart/";

    internal static bool IsDimmModule(string hardwareIdentifier) =>
        hardwareIdentifier.StartsWith(DimmIdentifierPrefix, StringComparison.Ordinal);

    internal static string BuildSmartStorageId(string hardwareIdentifier) =>
        SmartStorageIdPrefix + hardwareIdentifier.TrimStart('/');

    internal static bool IsSmartStorageComponent(string componentId) =>
        componentId.StartsWith(SmartStorageIdPrefix, StringComparison.Ordinal);
}
