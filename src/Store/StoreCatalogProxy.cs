using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Widgets;

namespace Nexus.Service.Store;

/// <summary>
/// Read-only passthrough to the cloud store catalog.
/// </summary>
/// <remarks>
/// The dashboard cannot call the cloud API directly: the service serves it under
/// a <c>connect-src 'self'</c> CSP, so a cross-origin fetch is blocked in the
/// browser. Server-side calls have no such limit, so the catalog is proxied
/// here rather than widening the policy.
///
/// Only the client's own facts are forwarded (its version, whether the surface
/// has a pointer), so the catalog can answer with versions this machine can
/// actually install.
/// </remarks>
public sealed class StoreCatalogProxy
{
    private readonly HttpClient _http;

    public const string DefaultCloudApi = "https://api.hellonexus.com";

    public StoreCatalogProxy(HttpClient http) => _http = http;

    public static string CloudApiBase()
    {
        var configured = Environment.GetEnvironmentVariable("NEXUS_CLOUD_API");
        return string.IsNullOrWhiteSpace(configured) ? DefaultCloudApi : configured.TrimEnd('/');
    }

    /// <summary>Storefront listing. Returns null when the catalog is unreachable.</summary>
    public async Task<string?> ListAsync(string? nexusVersion, bool? touch, CancellationToken ct) =>
        RewriteMedia(await GetAsync($"/store/apps{Query(nexusVersion, touch)}", ct));

    /// <summary>One app's store page. Returns null when unreachable or unknown.</summary>
    public async Task<string?> DetailAsync(string appId, string? nexusVersion, bool? touch, CancellationToken ct)
    {
        if (!AppIds.IsValid(appId)) return null;
        return RewriteMedia(await GetAsync($"/store/apps/{appId}{Query(nexusVersion, touch)}", ct));
    }

    /// <summary>
    /// Points the catalog's absolute media URLs at this service's own proxy. The
    /// dashboard's img-src is 'self', so a cross-origin asset host would render
    /// as a broken image; proxying keeps the cloud contract absolute (a web
    /// storefront wants that) while the in-app client stays same-origin.
    /// </summary>
    public static string? RewriteMedia(string? json)
    {
        if (string.IsNullOrEmpty(json)) return json;
        return json.Replace($"{AssetsBase()}/apps/", "/apps-api/store/media/", StringComparison.Ordinal);
    }

    /// <summary>Assets host the media proxy is allowed to fetch from.</summary>
    public static string AssetsBase()
    {
        var configured = Environment.GetEnvironmentVariable("NEXUS_STORE_ASSETS");
        return string.IsNullOrWhiteSpace(configured) ? StoreInstaller.DefaultAssetsBase : configured.TrimEnd('/');
    }

    /// <summary>
    /// Streams one media object. The path is confined to the assets host's
    /// apps/ prefix, so this can never be turned into a general fetch proxy.
    /// </summary>
    public async Task<(byte[] bytes, string contentType)?> MediaAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains("..", StringComparison.Ordinal)) return null;
        try
        {
            using var res = await _http.GetAsync($"{AssetsBase()}/apps/{path}", ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            var bytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var type = res.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            return (bytes, type);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[store] media {path} failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Artifact location and hash for a version, which is what the installer
    /// verifies against. A caller never gets to supply those itself.
    /// </summary>
    public Task<string?> DownloadInfoAsync(string appId, string? version, string? nexusVersion, CancellationToken ct)
    {
        if (!AppIds.IsValid(appId)) return Task.FromResult<string?>(null);
        var q = $"?nexusVersion={Uri.EscapeDataString(nexusVersion ?? "")}";
        if (!string.IsNullOrEmpty(version)) q += $"&version={Uri.EscapeDataString(version)}";
        return GetAsync($"/store/apps/{appId}/download{q}", ct);
    }

    private static string Query(string? nexusVersion, bool? touch)
    {
        var q = $"?nexusVersion={Uri.EscapeDataString(nexusVersion ?? "")}";
        if (touch.HasValue) q += $"&touch={(touch.Value ? "true" : "false")}";
        return q;
    }

    private async Task<string?> GetAsync(string path, CancellationToken ct)
    {
        try
        {
            using var res = await _http.GetAsync(CloudApiBase() + path, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            return await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[store] catalog {path} failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
