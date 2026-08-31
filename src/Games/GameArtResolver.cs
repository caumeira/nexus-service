using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;

namespace Nexus.Service.Games;

/// <summary>Cover art for one game: a CDN url the client loads directly, or icon bytes the service extracted.</summary>
public sealed record GameArt(string Url, byte[] Bytes)
{
    public static readonly GameArt None = new("", Array.Empty<byte>());

    public bool IsEmpty => Url.Length == 0 && Bytes.Length == 0;
}

/// <summary>Resolves cover art for a played game; GameArt.None on any miss so the caller keeps its placeholder.</summary>
public interface IGameArtResolver
{
    Task<GameArt> ResolveAsync(string gameKey, CancellationToken ct);
}

/// <summary>
/// Steam art comes from the store's own appdetails record rather than a
/// constructed cdn path: newer titles only exist under
/// store_item_assets/steam/apps/{appid}/{content hash}/, and the hash cannot
/// be derived from the appid (measured: appid 2473350 404s on every legacy
/// path). The legacy capsule stays the fallback, since it still answers for
/// most of the back catalogue and costs no round trip.
///
/// Everything else falls back to the game executable's own icon. Epic exposes
/// no keyless route to its key art - the storefront GraphQL is Cloudflare-
/// gated (403), the catalog service needs an OAuth token (401), and the one
/// open endpoint is keyed by a store slug the local manifest does not carry.
/// </summary>
public sealed class GameArtResolver : IGameArtResolver
{
    private const string DefaultStoreBaseUrl = "https://store.steampowered.com";
    private const int CacheCap = 64;

    private static readonly TimeSpan PositiveTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan MissTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    private readonly IHttpClientFactory _http;
    private readonly GameCatalog _catalog;
    private readonly IProcessIconProvider _icons;
    private readonly string _storeBaseUrl;

    private readonly Dictionary<string, (GameArt Art, DateTime ExpiresUtc)> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public GameArtResolver(IHttpClientFactory http, GameCatalog catalog, IProcessIconProvider icons)
        : this(http, catalog, icons, DefaultStoreBaseUrl)
    {
    }

    // Test-only ctor: loopback store base url.
    internal GameArtResolver(IHttpClientFactory http, GameCatalog catalog, IProcessIconProvider icons, string storeBaseUrl)
    {
        _http = http;
        _catalog = catalog;
        _icons = icons;
        _storeBaseUrl = storeBaseUrl.TrimEnd('/');
    }

    public async Task<GameArt> ResolveAsync(string gameKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gameKey)) return GameArt.None;

        if (TryReadCache(gameKey, out var cached)) return cached;

        var art = await ResolveUncachedAsync(gameKey, ct).ConfigureAwait(false);
        var ttl = art.IsEmpty ? MissTtl : PositiveTtl;
        Store(gameKey, art, ttl);
        return art;
    }

    private async Task<GameArt> ResolveUncachedAsync(string gameKey, CancellationToken ct)
    {
        if (TryParseSteamAppId(gameKey, out var appId))
        {
            var resolved = await ResolveSteamAsync(appId, ct).ConfigureAwait(false);
            if (resolved.Length > 0) return new GameArt(resolved, Array.Empty<byte>());
            return new GameArt(LegacyCapsuleUrl(appId), Array.Empty<byte>());
        }

        var icon = ResolveExecutableIcon(gameKey);
        return icon.Length > 0 ? new GameArt("", icon) : GameArt.None;
    }

    /// <summary>The store's own header image url, or empty when the record cannot be read.</summary>
    private async Task<string> ResolveSteamAsync(int appId, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);

            var client = _http.CreateClient();
            var url = $"{_storeBaseUrl}/api/appdetails?appids={appId}&filters=basic";
            using var response = await client.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return "";

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty(appId.ToString(), out var entry)) return "";
            if (!entry.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True) return "";
            if (!entry.TryGetProperty("data", out var data)) return "";
            if (!data.TryGetProperty("header_image", out var header) || header.ValueKind != JsonValueKind.String) return "";

            var image = header.GetString() ?? "";
            // The ?t= cache-buster changes whenever the publisher re-uploads,
            // which would defeat the browser cache across our own TTL.
            var query = image.IndexOf('?');
            if (query >= 0) image = image[..query];
            return image.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? image : "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return "";
        }
    }

    /// <summary>PNG bytes of the game executable's icon, or empty when there is nothing to read.</summary>
    private byte[] ResolveExecutableIcon(string gameKey)
    {
        if (!_catalog.TryGetInstallDir(gameKey, out var installDir)) return Array.Empty<byte>();

        var exe = PickGameExecutable(installDir);
        if (exe.Length == 0) return Array.Empty<byte>();

        return _icons.GetIcon(exe) ?? Array.Empty<byte>();
    }

    // Below this an executable name is too short to be evidence: "T.exe" would
    // otherwise claim any folder starting with a T.
    private const int MinNameMatchLength = 4;

    /// <summary>
    /// The executable most likely to carry the game's own icon: one named after
    /// the install folder wins, since a game's shipping binary usually is, and
    /// the largest file is the fallback - launchers, crash handlers and
    /// redistributables sit beside it but are far smaller.
    /// </summary>
    internal static string PickGameExecutable(string installDir)
    {
        try
        {
            if (!Directory.Exists(installDir)) return "";

            var exes = Directory
                .EnumerateFiles(installDir, "*.exe", SearchOption.TopDirectoryOnly)
                .ToList();
            if (exes.Count == 0) return "";

            var folder = Slug(Path.GetFileName(installDir.TrimEnd(Path.DirectorySeparatorChar)));
            var named = exes
                .Where(e => NameMatches(Slug(Path.GetFileNameWithoutExtension(e)), folder))
                .ToList();

            return (named.Count > 0 ? named : exes)
                .OrderByDescending(e => new FileInfo(e).Length)
                .First();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    // Prefix either way, not equality: a "Huntdown Overtime" folder ships
    // Huntdown.exe, and a versioned folder appends to the game's own name.
    private static bool NameMatches(string exe, string folder) =>
        exe.Length >= MinNameMatchLength
        && (exe == folder || folder.StartsWith(exe, StringComparison.Ordinal) || exe.StartsWith(folder, StringComparison.Ordinal));

    private static string Slug(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var length = 0;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch)) buffer[length++] = char.ToLowerInvariant(ch);
        }
        return new string(buffer[..length]);
    }

    internal static bool TryParseSteamAppId(string gameKey, out int appId)
    {
        appId = 0;
        const string prefix = "steam:";
        if (!gameKey.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return int.TryParse(gameKey.AsSpan(prefix.Length), out appId) && appId > 0;
    }

    internal static string LegacyCapsuleUrl(int appId) =>
        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/capsule_231x87.jpg";

    private bool TryReadCache(string gameKey, out GameArt art)
    {
        _lock.Wait();
        try
        {
            if (_cache.TryGetValue(gameKey, out var hit) && DateTime.UtcNow < hit.ExpiresUtc)
            {
                art = hit.Art;
                return true;
            }
        }
        finally { _lock.Release(); }

        art = GameArt.None;
        return false;
    }

    private void Store(string gameKey, GameArt art, TimeSpan ttl)
    {
        _lock.Wait();
        try
        {
            if (_cache.Count >= CacheCap && !_cache.ContainsKey(gameKey))
            {
                var oldest = _cache.OrderBy(e => e.Value.ExpiresUtc).First().Key;
                _cache.Remove(oldest);
            }
            _cache[gameKey] = (art, DateTime.UtcNow.Add(ttl));
        }
        finally { _lock.Release(); }
    }
}
