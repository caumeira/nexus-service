using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Widgets;

/// <summary>
/// Install one version of a store app. The caller names what to install and what
/// it must hash to; it never supplies a URL, so a page cannot point the service
/// at an arbitrary host (the URL is composed from the configured assets base).
/// </summary>
public sealed class StoreInstallRequest
{
    [JsonPropertyName("appId")] public string? AppId { get; set; }

    /// <summary>Semver of the version to install, matching the artifact key.</summary>
    [JsonPropertyName("version")] public string? Version { get; set; }

    /// <summary>Lowercase hex SHA-256 of the artifact, from the store catalog. Required: it is the trust pin.</summary>
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }

    /// <summary>Artifact size in bytes. Checked before hashing; 0 skips the pre-check.</summary>
    [JsonPropertyName("size")] public long Size { get; set; }
}

public sealed class StoreInstallResponse
{
    [JsonPropertyName("appId")] public string AppId { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }

    /// <summary>
    /// Machine-readable failure: invalid_app_id, invalid_version, missing_hash,
    /// artifact_unavailable, hash_mismatch, bad_archive, manifest_mismatch,
    /// no_user_apps_dir, or install_failed.
    /// </summary>
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}
