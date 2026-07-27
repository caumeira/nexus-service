using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

/// <summary>Resolves a higher-resolution album cover for a now-playing song; empty bytes on any miss.</summary>
public interface IAlbumArtHdResolver
{
    Task<byte[]> GetHdAlbumArtAsync(MediaSong song);
}

/// <summary>
/// Best-effort HD album art via the keyless iTunes Search API: search by
/// artist + title (song entity), fall back to artist + album, token-verify
/// the match, rewrite the 100x100 thumbnail URL to a high-resolution
/// variant, download. A song result whose album does not match is held back
/// until every attempt has failed to produce an exact match. Every failure
/// path returns empty bytes so callers keep the OS-provided thumbnail.
/// Positive results cache as bytes (small LRU); a verified miss
/// negative-caches for the full TTL, an upstream failure only briefly.
/// </summary>
public sealed class AlbumArtHdResolver : IAlbumArtHdResolver
{
    private const string DefaultBaseUrl = "https://itunes.apple.com";
    private const int PositiveCacheCap = 8;
    private const int NegativeCacheCap = 128;
    private const long MaxArtBytes = 8_000_000;
    private const double MatchThreshold = 0.6;

    private static readonly TimeSpan DefaultNegativeTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan DefaultFailureTtl = TimeSpan.FromMinutes(2);

    private readonly IHttpClientFactory _http;
    private readonly string _baseUrl;
    private readonly TimeSpan _negativeTtl;
    private readonly TimeSpan _failureTtl;

    private readonly Dictionary<string, (byte[] Bytes, DateTime FetchedUtc)> _cache = new();
    private readonly Dictionary<string, DateTime> _missExpiries = new();
    private readonly Dictionary<string, Task<byte[]>> _inFlight = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AlbumArtHdResolver(IHttpClientFactory http)
        : this(http, DefaultBaseUrl, DefaultNegativeTtl, DefaultFailureTtl)
    {
    }

    // Test-only ctor: loopback base URL + short TTLs.
    internal AlbumArtHdResolver(IHttpClientFactory http, string baseUrl, TimeSpan negativeTtl, TimeSpan? failureTtl = null)
    {
        _http = http;
        _baseUrl = baseUrl;
        _negativeTtl = negativeTtl;
        _failureTtl = failureTtl ?? DefaultFailureTtl;
    }

    public async Task<byte[]> GetHdAlbumArtAsync(MediaSong song)
    {
        var artist = song.Artist.Trim();
        var album = song.Album.Trim();
        var title = song.Title.Trim();
        if (artist.Length == 0 || (album.Length == 0 && title.Length == 0))
        {
            return Array.Empty<byte>();
        }

        var key = $"{NormalizeForCompare(artist)}\u001f{NormalizeForCompare(album.Length > 0 ? album : title)}";

        Task<byte[]>? existingFetch = null;
        TaskCompletionSource<byte[]>? owned = null;

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out var hit))
            {
                _cache[key] = (hit.Bytes, DateTime.UtcNow);
                return hit.Bytes;
            }
            if (_missExpiries.TryGetValue(key, out var missExpiry) && DateTime.UtcNow < missExpiry)
            {
                return Array.Empty<byte>();
            }
            if (!_inFlight.TryGetValue(key, out existingFetch))
            {
                owned = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlight[key] = owned.Task;
            }
        }
        finally
        {
            _lock.Release();
        }

        if (existingFetch is not null)
        {
            return await existingFetch.ConfigureAwait(false);
        }

        var bytes = Array.Empty<byte>();
        var definitive = true;
        try
        {
            try
            {
                (bytes, definitive) = await ResolveAsync(artist, album, title).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                definitive = false;
                Console.Error.WriteLine($"[album-art-hd] resolve failed: {ex.Message}");
            }

            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (bytes.Length > 0)
                {
                    WriteCacheLocked(key, bytes);
                }
                else
                {
                    WriteMissLocked(key, definitive ? _negativeTtl : _failureTtl);
                }
                _inFlight.Remove(key);
            }
            finally
            {
                _lock.Release();
            }
        }
        finally
        {
            // Coalesced waiters must always be released, whatever the
            // bookkeeping above did.
            owned!.TrySetResult(bytes);
        }
        return bytes;
    }

    private async Task<(byte[] Bytes, bool Definitive)> ResolveAsync(string artist, string album, string title)
    {
        using var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        client.MaxResponseContentBufferSize = MaxArtBytes;
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", Nexus.Service.Widgets.AppProxyService.DefaultUserAgent);

        var failed = false;
        string? deferredFallback = null;
        foreach (var (term, byAlbum) in BuildAttempts(artist, album, title))
        {
            var outcome = await SearchVerifiedArtworkAsync(client, term, byAlbum, artist, byAlbum ? album : title, album)
                .ConfigureAwait(false);
            failed |= outcome.Failed;
            if (outcome.Exact is not null)
            {
                var bytes = await DownloadArtAsync(client, outcome.Exact).ConfigureAwait(false);
                if (bytes.Length > 0)
                {
                    return (bytes, true);
                }
                // Verified match with no downloadable art: worth retrying soon.
                failed = true;
            }
            deferredFallback ??= outcome.Fallback;
        }

        if (deferredFallback is not null)
        {
            var bytes = await DownloadArtAsync(client, deferredFallback).ConfigureAwait(false);
            if (bytes.Length > 0)
            {
                return (bytes, true);
            }
            failed = true;
        }

        return (Array.Empty<byte>(), !failed);
    }

    // Song search leads: the title is the most precise key (the album search
    // ranks remix EPs and singles above the plain album for many queries),
    // and a song result carries its album name for cross-checking.
    /// <summary>Search attempts in order: raw artist+title, decoration-stripped variant, then the same pair for artist+album.</summary>
    internal static List<(string Term, bool ByAlbum)> BuildAttempts(string artist, string album, string title)
    {
        var attempts = new List<(string Term, bool ByAlbum)>();
        void Add(string a, string b, bool byAlbum)
        {
            var term = CollapseWhitespace($"{a} {b}");
            if (term.Length == 0)
            {
                return;
            }
            foreach (var existing in attempts)
            {
                if (existing.Term == term && existing.ByAlbum == byAlbum)
                {
                    return;
                }
            }
            attempts.Add((term, byAlbum));
        }

        if (title.Length > 0)
        {
            Add(artist, title, byAlbum: false);
            Add(StripDecorations(artist), StripDecorations(title), byAlbum: false);
        }
        if (album.Length > 0)
        {
            Add(artist, album, byAlbum: true);
            Add(StripDecorations(artist), StripDecorations(album), byAlbum: true);
        }
        return attempts;
    }

    /// <summary>Exact = artist and name verified (and, for songs, the album too when one is known); Fallback = verified song on the wrong release; Failed = upstream error, not a verified miss.</summary>
    private readonly record struct SearchOutcome(string? Exact, string? Fallback, bool Failed);

    private async Task<SearchOutcome> SearchVerifiedArtworkAsync(
        HttpClient client, string term, bool byAlbum, string expectedArtist, string expectedName, string expectedAlbum)
    {
        var entity = byAlbum ? "album" : "song";
        var url = $"{_baseUrl}/search?media=music&limit=5&entity={entity}&term={Uri.EscapeDataString(term)}";
        try
        {
            using var resp = await client.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[album-art-hd] search returned {(int)resp.StatusCode}");
                return new SearchOutcome(null, null, Failed: true);
            }

            // The endpoint responds with Content-Type text/javascript, which
            // ReadFromJsonAsync rejects - deserialize from the raw string.
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var payload = JsonSerializer.Deserialize(
                json, Nexus.Service.Serialization.AppJsonContext.Default.ItunesSearchResponse);
            if (payload?.Results is not { Length: > 0 } results)
            {
                return new SearchOutcome(null, null, Failed: false);
            }

            string? fallback = null;
            foreach (var result in results)
            {
                if (string.IsNullOrEmpty(result.ArtworkUrl100) || !TokensMatch(expectedArtist, result.ArtistName))
                {
                    continue;
                }
                var name = byAlbum ? result.CollectionName : result.TrackName;
                if (!NameMatches(expectedName, name))
                {
                    continue;
                }
                // A song result whose album also matches carries the right
                // cover for the playing release; one that does not (remix EP,
                // compilation, DJ mix) is only a fallback - the caller holds
                // it back until every attempt is exhausted.
                if (byAlbum || expectedAlbum.Length == 0 || NameMatches(expectedAlbum, result.CollectionName))
                {
                    return new SearchOutcome(result.ArtworkUrl100, null, Failed: false);
                }
                fallback ??= result.ArtworkUrl100;
            }
            return new SearchOutcome(null, fallback, Failed: false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[album-art-hd] search failed: {ex.Message}");
            return new SearchOutcome(null, null, Failed: true);
        }
    }

    // The catalog serves arbitrary sizes by URL path; a size missing upstream
    // falls back to a smaller one before giving up.
    private static async Task<byte[]> DownloadArtAsync(HttpClient client, string artworkUrl100)
    {
        var bytes = await DownloadAsync(client, RewriteArtworkSize(artworkUrl100, "1200x1200")).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            bytes = await DownloadAsync(client, RewriteArtworkSize(artworkUrl100, "600x600")).ConfigureAwait(false);
        }
        return bytes;
    }

    private static async Task<byte[]> DownloadAsync(HttpClient client, string? url)
    {
        if (url is null)
        {
            return Array.Empty<byte>();
        }
        try
        {
            using var resp = await client.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return Array.Empty<byte>();
            }
            return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[album-art-hd] download failed: {ex.Message}");
            return Array.Empty<byte>();
        }
    }

    /// <summary>Rewrites the trailing 100x100 segment of an artworkUrl100; null when the URL has no such segment.</summary>
    internal static string? RewriteArtworkSize(string artworkUrl100, string size)
    {
        var idx = artworkUrl100.LastIndexOf("100x100", StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }
        return string.Concat(artworkUrl100.AsSpan(0, idx), size, artworkUrl100.AsSpan(idx + "100x100".Length));
    }

    // Lenient by design: multi-artist strings ("Artist A, Artist B") must
    // still match a single credited artist.
    /// <summary>Token containment over the smaller side; both sides must produce at least one token.</summary>
    internal static bool TokensMatch(string? expected, string? actual)
    {
        var a = Tokenize(expected);
        var b = Tokenize(actual);
        if (a.Count == 0 || b.Count == 0)
        {
            return false;
        }
        var small = a.Count <= b.Count ? a : b;
        var large = a.Count <= b.Count ? b : a;
        var overlap = 0;
        foreach (var token in small)
        {
            if (large.Contains(token))
            {
                overlap++;
            }
        }
        return overlap / (double)small.Count >= MatchThreshold;
    }

    // Strict by design: containment alone lets "Currents B-Sides & Remixes"
    // swallow "Currents"; Jaccard over decoration-stripped tokens rejects a
    // result whose name carries extra substance beyond edition noise.
    /// <summary>Token Jaccard over decoration-stripped names; both sides must produce at least one token.</summary>
    internal static bool NameMatches(string? expected, string? actual)
    {
        var a = Tokenize(StripDecorations(expected ?? ""));
        var b = Tokenize(StripDecorations(actual ?? ""));
        if (a.Count == 0 || b.Count == 0)
        {
            return false;
        }
        var overlap = 0;
        foreach (var token in a)
        {
            if (b.Contains(token))
            {
                overlap++;
            }
        }
        var union = a.Count + b.Count - overlap;
        return overlap / (double)union >= MatchThreshold;
    }

    private static HashSet<string> Tokenize(string? value)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(value))
        {
            return tokens;
        }
        var normalized = NormalizeForCompare(value);
        foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            tokens.Add(token);
        }
        return tokens;
    }

    /// <summary>Lowercase, Latin diacritics folded, punctuation collapsed to spaces.</summary>
    internal static string NormalizeForCompare(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var raw in value)
        {
            var ch = char.ToLowerInvariant(raw);
            if (char.IsLetterOrDigit(ch))
            {
                AppendFolded(sb, ch);
            }
            else
            {
                sb.Append(' ');
            }
        }
        return CollapseWhitespace(sb.ToString());
    }

    // InvariantGlobalization strips ICU, making string.Normalize(FormD) a
    // no-op, so accents are folded by table: Latin-1 Supplement + the common
    // Latin Extended-A letters. Other scripts pass through and compare exact.
    private static void AppendFolded(StringBuilder sb, char ch)
    {
        switch (ch)
        {
            case 'æ': sb.Append("ae"); return;
            case 'œ': sb.Append("oe"); return;
            case 'ß': sb.Append("ss"); return;
            case 'þ': sb.Append("th"); return;
            case 'ð' or 'ď' or 'đ': sb.Append('d'); return;
            case (>= 'à' and <= 'å') or 'ā' or 'ă' or 'ą': sb.Append('a'); return;
            case 'ç' or 'ć' or 'ĉ' or 'ċ' or 'č': sb.Append('c'); return;
            case (>= 'è' and <= 'ë') or 'ē' or 'ĕ' or 'ė' or 'ę' or 'ě': sb.Append('e'); return;
            case 'ĝ' or 'ğ' or 'ġ' or 'ģ': sb.Append('g'); return;
            case 'ĥ' or 'ħ': sb.Append('h'); return;
            case (>= 'ì' and <= 'ï') or 'ĩ' or 'ī' or 'ĭ' or 'į' or 'ı': sb.Append('i'); return;
            case 'ĵ': sb.Append('j'); return;
            case 'ķ': sb.Append('k'); return;
            case 'ĺ' or 'ļ' or 'ľ' or 'ŀ' or 'ł': sb.Append('l'); return;
            case 'ñ' or 'ń' or 'ņ' or 'ň': sb.Append('n'); return;
            case (>= 'ò' and <= 'ö') or 'ø' or 'ō' or 'ŏ' or 'ő': sb.Append('o'); return;
            case 'ŕ' or 'ŗ' or 'ř': sb.Append('r'); return;
            case 'ś' or 'ŝ' or 'ş' or 'š': sb.Append('s'); return;
            case 'ţ' or 'ť' or 'ŧ': sb.Append('t'); return;
            case (>= 'ù' and <= 'ü') or 'ũ' or 'ū' or 'ŭ' or 'ů' or 'ű' or 'ų': sb.Append('u'); return;
            case 'ŵ': sb.Append('w'); return;
            case 'ý' or 'ÿ' or 'ŷ': sb.Append('y'); return;
            case 'ź' or 'ż' or 'ž': sb.Append('z'); return;
            default: sb.Append(ch); return;
        }
    }

    // Decoration vocabulary: a parenthesized/bracketed group or a " - " suffix
    // segment containing one of these tokens is store-listing noise, not part
    // of the canonical name (Spotify styles remasters as "Song - 2011 Remaster").
    private static readonly HashSet<string> DecorationTokens = new(StringComparer.Ordinal)
    {
        "feat", "featuring", "ft", "with",
        "remaster", "remastered", "deluxe", "edition", "expanded", "bonus",
        "anniversary", "live", "mono", "stereo", "single", "version", "remix",
        "edit", "explicit", "clean", "ep", "soundtrack",
    };

    /// <summary>Removes bracketed groups and dash-suffix segments made of decoration vocabulary; returns the input trimmed otherwise.</summary>
    internal static string StripDecorations(string value)
    {
        var sb = new StringBuilder(value.Length);
        var groupStart = -1;
        var depth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch is '(' or '[')
            {
                if (depth == 0)
                {
                    groupStart = sb.Length;
                }
                depth++;
                sb.Append(ch);
            }
            else if (ch is ')' or ']')
            {
                sb.Append(ch);
                if (depth > 0 && --depth == 0)
                {
                    var group = sb.ToString(groupStart, sb.Length - groupStart);
                    if (ContainsDecorationToken(group))
                    {
                        sb.Length = groupStart;
                    }
                    groupStart = -1;
                }
            }
            else
            {
                sb.Append(ch);
            }
        }

        var result = sb.ToString();

        var dashIdx = result.IndexOf(" - ", StringComparison.Ordinal);
        while (dashIdx >= 0)
        {
            if (ContainsDecorationToken(result[(dashIdx + 3)..]))
            {
                result = result[..dashIdx];
                break;
            }
            dashIdx = result.IndexOf(" - ", dashIdx + 3, StringComparison.Ordinal);
        }

        var featIdx = FirstDecorationClauseIndex(result);
        if (featIdx > 0)
        {
            result = result[..featIdx];
        }

        return CollapseWhitespace(result);
    }

    private static bool ContainsDecorationToken(string segment)
    {
        foreach (var token in Tokenize(segment))
        {
            if (DecorationTokens.Contains(token))
            {
                return true;
            }
        }
        return false;
    }

    // Bare "feat."/"featuring" clauses (no brackets) cut from the marker to the
    // end; only featuring markers qualify - other decoration tokens mid-string
    // are legitimate words ("Live Forever").
    private static int FirstDecorationClauseIndex(string value)
    {
        foreach (var marker in new[] { " feat. ", " feat ", " featuring ", " ft. " })
        {
            var idx = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                return idx;
            }
        }
        return -1;
    }

    private static string CollapseWhitespace(string value)
    {
        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    // Caller must hold _lock. Positive entries never expire (a cover is
    // immutable); FetchedUtc doubles as last-access for LRU eviction.
    private void WriteCacheLocked(string key, byte[] bytes)
    {
        _cache[key] = (bytes, DateTime.UtcNow);
        while (_cache.Count > PositiveCacheCap)
        {
            string? oldest = null;
            var oldestAt = DateTime.MaxValue;
            foreach (var entry in _cache)
            {
                if (entry.Value.FetchedUtc < oldestAt)
                {
                    oldestAt = entry.Value.FetchedUtc;
                    oldest = entry.Key;
                }
            }
            _cache.Remove(oldest!);
        }
        _missExpiries.Remove(key);
    }

    // Caller must hold _lock.
    private void WriteMissLocked(string key, TimeSpan ttl)
    {
        _missExpiries[key] = DateTime.UtcNow + ttl;
        while (_missExpiries.Count > NegativeCacheCap)
        {
            string? oldest = null;
            var oldestAt = DateTime.MaxValue;
            foreach (var entry in _missExpiries)
            {
                if (entry.Value < oldestAt)
                {
                    oldestAt = entry.Value;
                    oldest = entry.Key;
                }
            }
            _missExpiries.Remove(oldest!);
        }
    }
}

public sealed class ItunesSearchResponse
{
    [JsonPropertyName("resultCount")] public int ResultCount { get; set; }
    [JsonPropertyName("results")] public ItunesSearchResult[]? Results { get; set; }
}

public sealed class ItunesSearchResult
{
    [JsonPropertyName("wrapperType")] public string? WrapperType { get; set; }
    [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
    [JsonPropertyName("collectionName")] public string? CollectionName { get; set; }
    [JsonPropertyName("trackName")] public string? TrackName { get; set; }
    [JsonPropertyName("artworkUrl100")] public string? ArtworkUrl100 { get; set; }
}
