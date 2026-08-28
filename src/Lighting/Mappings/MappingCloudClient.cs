using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Telemetry;

namespace Nexus.Service.Lighting.Mappings;

/// <summary>
/// Registry client. Reads (mapping lists) are anonymous, cached on disk, and
/// fully offline-tolerant - lighting never waits on the network and a dead
/// backend degrades to the cache. Writes (publish, adopt, revoke,
/// devices-seen) are adoption/ownership signals gated by the anonymous
/// install id, which is null when the user opted out of anonymous data;
/// opted-out installs still browse and apply.
/// </summary>
public sealed class MappingCloudClient
{
    private const string DefaultBaseUrl = "https://api.hellonexus.com";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _http;
    private readonly IConfigStore _store;
    private readonly string _baseUrl;
    private readonly string _cacheDir;

    public MappingCloudClient(IHttpClientFactory http, IConfigStore store)
    {
        _http = http;
        _store = store;
        _baseUrl = Environment.GetEnvironmentVariable("NEXUS_API_BASE")?.TrimEnd('/') ?? DefaultBaseUrl;
        _cacheDir = Path.Combine(JsonConfigStore.ResolveDataDirectory(), "mappings-cache");
    }

    public sealed class ListResult
    {
        public List<CommunityMapping> Items { get; init; } = new();
        public bool Offline { get; init; }
    }

    /// <summary>Ranked community mappings for a device key. Serves a fresh disk cache without touching the network; falls back to stale cache (Offline=true) when the registry is unreachable.</summary>
    public async Task<ListResult> GetMappingsAsync(string deviceKey, bool forceRefresh, CancellationToken ct)
    {
        // Same shape as a machine with no network, not an error.
        if (!Common.ClientCredential.IsOfficial)
            return new ListResult { Offline = true };

        if (string.IsNullOrEmpty(deviceKey))
            return new ListResult();

        var cachePath = CachePathFor(deviceKey);
        if (!forceRefresh && TryReadCache(cachePath, out var cached, out var age) && age < CacheTtl)
            return new ListResult { Items = cached.Items };

        try
        {
            using var client = CreateClient();
            var url = $"{_baseUrl}/mappings?deviceKey={Uri.EscapeDataString(deviceKey)}";
            var list = await client.GetFromJsonAsync(
                url, AppJsonContext.Default.CommunityMappingList, ct).ConfigureAwait(false);
            var result = list ?? new CommunityMappingList();
            WriteCache(cachePath, result);
            return new ListResult { Items = result.Items };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[mappings] list fetch failed for {deviceKey}: {ex.GetType().Name}: {ex.Message}");
            if (TryReadCache(cachePath, out var stale, out _))
                return new ListResult { Items = stale.Items, Offline = true };
            return new ListResult { Offline = true };
        }
    }

    /// <summary>Disk-cache-only mapping count for a device key. Never touches the network; used by the device-card badge so list rendering stays offline-fast.</summary>
    public int CachedMappingCount(string deviceKey)
    {
        if (string.IsNullOrEmpty(deviceKey))
            return 0;
        return TryReadCache(CachePathFor(deviceKey), out var cached, out _) ? cached.Items.Count : 0;
    }

    /// <summary>Publish the artifact. Returns null when the user opted out of anonymous data (publishing requires the install id) or the registry rejected/was unreachable.</summary>
    public async Task<PublishMappingResponse?> PublishAsync(MappingArtifact artifact, string name, string? description, string? authorName, CancellationToken ct)
    {
        if (!Common.ClientCredential.IsOfficial)
            return null;

        var installId = InstallIdentity.Resolve(_store);
        if (installId is null)
            return null;
        try
        {
            using var client = CreateClient();
            var body = new PublishCloudBody
            {
                InstallId = installId,
                Artifact = artifact,
                Name = name,
                Description = description,
                AuthorName = authorName,
            };
            using var content = JsonContent.Create(body, AppJsonContext.Default.PublishCloudBody);
            using var res = await client.PostAsync($"{_baseUrl}/mappings", content, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[mappings] publish rejected: {(int)res.StatusCode}");
                return null;
            }
            var created = await res.Content.ReadFromJsonAsync(
                AppJsonContext.Default.CommunityMapping, ct).ConfigureAwait(false);
            return new PublishMappingResponse
            {
                MappingId = created?.Id,
                AlreadyExisted = res.StatusCode == System.Net.HttpStatusCode.OK,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[mappings] publish failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public Task AdoptAsync(string mappingId, string deviceKey, bool auto, CancellationToken ct)
        => SignalAsync($"/mappings/{Uri.EscapeDataString(mappingId)}/adopt", installId => JsonContent.Create(
            new AdoptCloudBody { InstallId = installId, DeviceKey = deviceKey, Source = auto ? "auto" : "manual" },
            AppJsonContext.Default.AdoptCloudBody), HttpMethod.Post, ct);

    public Task RevokeAsync(string mappingId, string reason, CancellationToken ct)
        => SignalAsync($"/mappings/{Uri.EscapeDataString(mappingId)}/adopt", installId => JsonContent.Create(
            new RevokeCloudBody { InstallId = installId, Reason = reason },
            AppJsonContext.Default.RevokeCloudBody), HttpMethod.Delete, ct);

    /// <summary>Ownership signal feeding the registry's publish-provenance and qualified-adoption gates.</summary>
    public Task ReportDevicesSeenAsync(IReadOnlyList<string> deviceKeys, CancellationToken ct)
    {
        if (deviceKeys.Count == 0)
            return Task.CompletedTask;
        return SignalAsync("/mappings/devices-seen", installId => JsonContent.Create(
            new DevicesSeenCloudBody { InstallId = installId, DeviceKeys = new List<string>(deviceKeys) },
            AppJsonContext.Default.DevicesSeenCloudBody), HttpMethod.Post, ct);
    }

    /// <summary>Best-effort fire of an adoption-style signal. Failures log and are dropped; signals are statistical, not transactional.</summary>
    private async Task SignalAsync(string path, Func<string, JsonContent> bodyFor, HttpMethod method, CancellationToken ct)
    {
        if (!Common.ClientCredential.IsOfficial)
            return;

        var installId = InstallIdentity.Resolve(_store);
        if (installId is null)
            return;
        try
        {
            using var client = CreateClient();
            using var request = new HttpRequestMessage(method, _baseUrl + path) { Content = bodyFor(installId) };
            using var res = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                Console.Error.WriteLine($"[mappings] signal {path} -> {(int)res.StatusCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[mappings] signal {path} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private HttpClient CreateClient()
    {
        var client = _http.CreateClient();
        client.Timeout = RequestTimeout;
        Common.ClientCredential.Apply(client);
        return client;
    }

    private string CachePathFor(string deviceKey)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(deviceKey)).AsSpan(0, 8)).ToLowerInvariant();
        return Path.Combine(_cacheDir, hash + ".json");
    }

    private static bool TryReadCache(string path, out CommunityMappingList list, out TimeSpan age)
    {
        list = new CommunityMappingList();
        age = TimeSpan.MaxValue;
        try
        {
            if (!File.Exists(path))
                return false;
            age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize(json, AppJsonContext.Default.CommunityMappingList);
            if (parsed is null)
                return false;
            list = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void WriteCache(string path, CommunityMappingList list)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            var json = JsonSerializer.Serialize(list, AppJsonContext.Default.CommunityMappingList);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mappings] cache write failed: {ex.Message}");
        }
    }
}
