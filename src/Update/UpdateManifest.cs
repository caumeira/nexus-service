using System;

namespace Nexus.Service.Update;

/// <summary>
/// Normalized update manifest returned by <see cref="IUpdateSource"/>.
/// Provider-specific shapes (GitHub JSON, S3, etc.) are mapped here before
/// reaching any consumer.
/// </summary>
public sealed class UpdateManifest
{
    /// <summary>Tag/version string, e.g. "v63".</summary>
    public string Version { get; init; } = "";
    /// <summary>Release notes (Markdown body).</summary>
    public string Notes { get; init; } = "";
    /// <summary>Direct download URL for the installer asset.</summary>
    public string AssetUrl { get; init; } = "";
    /// <summary>Lowercase hex SHA-256. Null when the provider has no hash.</summary>
    public string? Sha256 { get; init; }
    /// <summary>True only when SHA-256 came from the author-published SHA256SUMS asset, not from the asset digest field.</summary>
    public bool Sha256IsFromSumsFile { get; init; }
    /// <summary>Expected installer size in bytes. Zero when unknown.</summary>
    public long AssetSize { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
}
