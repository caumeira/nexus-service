namespace Nexus.Service.Telemetry;

/// <summary>Fleet event body posted to nexus-api's /telemetry/events. Shape is
/// a cross-language contract with nexus-api; do not change field names or
/// types without updating that side. Serialized camelCase via
/// AppJsonContext.</summary>
public sealed class FleetEventPayload
{
    public string InstallId { get; set; } = "";

    /// <summary>One of <see cref="TelemetryEvents.Install"/>,
    /// <see cref="TelemetryEvents.Specs"/>, <see cref="TelemetryEvents.OptOut"/>,
    /// <see cref="TelemetryEvents.OptIn"/>.</summary>
    public string Type { get; set; } = "";
    public string Version { get; set; } = "";
    public string Os { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string Arch { get; set; } = "";
    public string DeviceType { get; set; } = "";

    /// <summary>Present only on a <see cref="TelemetryEvents.Specs"/> event.</summary>
    public FleetEventSpecs? Specs { get; set; }
}

/// <summary>Coarse hardware summary attached to a "specs" fleet event. Reuses
/// the same display-string fields as the Devices -> System Specs tab; RAM is
/// converted to bytes from the parsed GB figure, so it is an approximation,
/// not an exact byte count.</summary>
public sealed class FleetEventSpecs
{
    public string Cpu { get; set; } = "";
    public string[] Gpu { get; set; } = System.Array.Empty<string>();
    public long RamBytes { get; set; }
    public string Motherboard { get; set; } = "";
}
