using System.Collections.Generic;
using Nexus.Service.Lighting.Mappings;

namespace Nexus.Service.Models.Devices;

// ----- /devices/lighting-devices/{id}/mappings - community mapping flow -----

/// <summary>
/// One community mapping as served by the registry (and proxied to the SPA).
/// Field set is the wire contract with nexus-api's mappings module.
/// </summary>
public sealed class CommunityMapping
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string DeviceKey { get; set; } = "";
    public string ContentHash { get; set; } = "";
    /// <summary>"community" | "verified"</summary>
    public string Origin { get; set; } = "community";
    /// <summary>True when the registry's eligibility gate allows silent auto-apply (score, age, undo rate, kill switch).</summary>
    public bool AutoApply { get; set; }
    public double Score { get; set; }
    public int AdopterCount { get; set; }
    public string? AuthorName { get; set; }
    public MappingArtifact? Payload { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
}

/// <summary>Registry list envelope: GET /mappings?deviceKey= returns { items: [...] }.</summary>
public sealed class CommunityMappingList
{
    public List<CommunityMapping> Items { get; set; } = new();
}

/// <summary>SPA-facing list for one local device, including local apply state.</summary>
public sealed class DeviceMappingsResponse : ApiResponse
{
    public string DeviceKey { get; set; } = "";
    /// <summary>True when the registry was unreachable and items come from the disk cache (possibly empty).</summary>
    public bool Offline { get; set; }
    public List<CommunityMapping> Items { get; set; } = new();
    public AppliedMappingSummary? Applied { get; set; }
    /// <summary>True when the user undid an auto-apply on this device; the client will not auto-apply again.</summary>
    public bool AutoApplyDeclined { get; set; }
}

public sealed class AppliedMappingSummary
{
    public string? MappingId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>"community" | "file"</summary>
    public string Source { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public bool AutoApplied { get; set; }
    public long AppliedAtMs { get; set; }
}

public sealed class ApplyMappingBody
{
    public string MappingId { get; set; } = "";
}

/// <summary>Device id -> cached community mapping count (only entries with a nonzero count).</summary>
public sealed class MappingsAvailableResponse : ApiResponse
{
    public Dictionary<string, int> Counts { get; set; } = new();
}

public sealed class PublishMappingResponse : ApiResponse
{
    public string? MappingId { get; set; }
    /// <summary>True when the registry already had an identical artifact for this device and returned the existing row.</summary>
    public bool AlreadyExisted { get; set; }
}

public sealed class ExportMappingResponse : ApiResponse
{
    public MappingArtifact? Artifact { get; set; }
}

// ----- cloud-bound request bodies (service -> nexus-api) -----

public sealed class PublishCloudBody
{
    public string InstallId { get; set; } = "";
    public MappingArtifact Artifact { get; set; } = new();
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? AuthorName { get; set; }
}

public sealed class AdoptCloudBody
{
    public string InstallId { get; set; } = "";
    public string DeviceKey { get; set; } = "";
    /// <summary>"manual" | "auto"</summary>
    public string Source { get; set; } = "manual";
}

public sealed class RevokeCloudBody
{
    public string InstallId { get; set; } = "";
    /// <summary>"undo" | "switched" | "reset"</summary>
    public string Reason { get; set; } = "switched";
}

public sealed class DevicesSeenCloudBody
{
    public string InstallId { get; set; } = "";
    public List<string> DeviceKeys { get; set; } = new();
}
