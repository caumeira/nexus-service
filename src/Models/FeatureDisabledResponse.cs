namespace Nexus.Service.Models;

/// <summary>409 body for a mutation blocked by a disabled feature pillar (lighting/cooling/monitoring/diagnostics).</summary>
public sealed record FeatureDisabledResponse
{
    public string Error { get; init; } = "feature_disabled";
    public string Feature { get; init; } = "";
}
