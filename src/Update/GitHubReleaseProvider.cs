using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Update;

/// <summary>
/// <see cref="IUpdateSource"/> backed by GitHub Releases. The only file in the
/// codebase that is aware of GitHub's REST shapes or snake_case DTOs.
///
/// Production channel: GET /repos/{owner}/{repo}/releases/latest (excludes
/// prereleases by design). Beta channel: GET /repos/{owner}/{repo}/releases
/// and picks the newest item (betas and stables, whichever is newest).
///
/// SHA-256: prefers the "SHA256SUMS" release asset; falls back to the
/// asset-level "digest" field ("sha256:{hex}") if SHA256SUMS is absent.
/// </summary>
public sealed class GitHubReleaseProvider : IUpdateSource
{
    // Releases are published to the public hello-nexus/nexus repo. Configurable
    // so a test repo or provider swap is a one-line change.
    public const string DefaultOwnerRepo = "hello-nexus/nexus";

    private readonly IHttpClientFactory _http;
    private readonly string _ownerRepo;

    public GitHubReleaseProvider(IHttpClientFactory http, string ownerRepo = DefaultOwnerRepo)
    {
        _http = http;
        _ownerRepo = ownerRepo;
    }

    public async Task<UpdateManifest?> GetLatestAsync(string channel, CancellationToken ct)
    {
        using var client = BuildClient();

        GitHubRelease? release = channel == "beta"
            ? await GetNewestBetaOrStableAsync(client, ct)
            : await GetLatestProductionAsync(client, ct);

        if (release is null) return null;

        var asset = SelectInstallerAsset(release.Assets);
        if (asset is null) return null;

        var (sha256, fromSumsFile) = await ResolveHashAsync(client, release, asset, ct);

        return new UpdateManifest
        {
            Version = release.TagName ?? "",
            Notes = release.Body ?? "",
            AssetUrl = asset.BrowserDownloadUrl ?? "",
            Sha256 = sha256,
            Sha256IsFromSumsFile = fromSumsFile,
            AssetSize = asset.Size,
            PublishedAt = release.PublishedAt,
        };
    }

    private async Task<GitHubRelease?> GetLatestProductionAsync(HttpClient client, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{_ownerRepo}/releases/latest";
        using var resp = await client.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync(AppJsonContext.Default.GitHubRelease, ct);
    }

    private async Task<GitHubRelease?> GetNewestBetaOrStableAsync(HttpClient client, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{_ownerRepo}/releases";
        using var resp = await client.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var releases = await resp.Content.ReadFromJsonAsync(AppJsonContext.Default.ListGitHubRelease, ct);
        // Newest by published date regardless of prerelease flag.
        return releases?.OrderByDescending(r => r.PublishedAt).FirstOrDefault();
    }

    private static async Task<(string? Hash, bool FromSumsFile)> ResolveHashAsync(
        HttpClient client,
        GitHubRelease release,
        GitHubReleaseAsset asset,
        CancellationToken ct)
    {
        var sumsAsset = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, "SHA256SUMS", StringComparison.OrdinalIgnoreCase));

        if (sumsAsset is not null && !string.IsNullOrEmpty(sumsAsset.BrowserDownloadUrl))
        {
            try
            {
                var text = await client.GetStringAsync(sumsAsset.BrowserDownloadUrl, ct);
                var hash = ParseSha256Sums(text, asset.Name ?? "");
                if (!string.IsNullOrEmpty(hash))
                {
                    return (hash, true);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[update] SHA256SUMS fetch failed: {ex.Message}");
            }
        }

        // Fallback: asset digest. Usable for manual installs, not for auto-stage.
        if (!string.IsNullOrEmpty(asset.Digest) &&
            asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return (asset.Digest["sha256:".Length..].ToLowerInvariant(), false);
        }

        return (null, false);
    }

    /// <summary>
    /// Selects the Windows installer asset. One Nexus-Setup*.exe per release,
    /// so a prefix match resolves the versioned name (Nexus-Setup-3.0.0.exe)
    /// and the legacy bare Nexus-Setup.exe.
    /// </summary>
    public static GitHubReleaseAsset? SelectInstallerAsset(IEnumerable<GitHubReleaseAsset> assets) =>
        assets.FirstOrDefault(a =>
            a.Name is not null
            && a.Name.StartsWith("Nexus-Setup", StringComparison.OrdinalIgnoreCase)
            && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Parses a SHA256SUMS file of the form "{hash}  {filename}" per line.
    /// Returns the hash for the given filename, or null if not found.
    /// </summary>
    public static string? ParseSha256Sums(string content, string filename)
    {
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Format: "<hash>  <filename>" or "<hash> <filename>" (one or two spaces).
            var trimmed = line.Trim();
            if (trimmed.Length < 66) continue;
            var hash = trimmed[..64];
            var rest = trimmed[64..].TrimStart();
            if (string.Equals(rest, filename, StringComparison.OrdinalIgnoreCase))
            {
                return hash.ToLowerInvariant();
            }
        }
        return null;
    }

    private HttpClient BuildClient()
    {
        var client = _http.CreateClient("GitHubOta");
        client.DefaultRequestHeaders.UserAgent.TryParseAdd("Nexus-Service/" + BuildInfo.Version);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }
}

// GitHub REST DTOs. snake_case properties must carry explicit [JsonPropertyName]
// attributes because the global AOT context policy is camelCase.

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubReleaseAsset> Assets { get; set; } = new();
}

public sealed class GitHubReleaseAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}
