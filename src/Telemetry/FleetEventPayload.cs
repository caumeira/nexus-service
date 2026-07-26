namespace Nexus.Service.Telemetry;

/// <summary>Fleet event body for nexus-api's /telemetry/events; a cross-language contract, serialized camelCase - do not rename fields without updating that side.</summary>
public sealed class FleetEventPayload
{
    public string InstallId { get; set; } = "";

    /// <summary>One of <see cref="TelemetryEvents.Install"/>, <see cref="TelemetryEvents.Specs"/>, <see cref="TelemetryEvents.OptOut"/>, <see cref="TelemetryEvents.OptIn"/>.</summary>
    public string Type { get; set; } = "";
    public string Version { get; set; } = "";
    public string Os { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string Arch { get; set; } = "";
    public string DeviceType { get; set; } = "";

    /// <summary>Present only on a <see cref="TelemetryEvents.Specs"/> event.</summary>
    public FleetEventSpecs? Specs { get; set; }
}

/// <summary>Coarse hardware summary for a "specs" fleet event; RamBytes is parsed-GB converted to bytes, an approximation, not an exact byte count.</summary>
public sealed class FleetEventSpecs
{
    public string Cpu { get; set; } = "";
    public string[] Gpu { get; set; } = System.Array.Empty<string>();
    public long RamBytes { get; set; }
    public string Motherboard { get; set; } = "";
}
