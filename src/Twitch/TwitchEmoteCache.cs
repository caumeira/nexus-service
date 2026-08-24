using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Nexus.Service.Twitch;

/// <summary>
/// Serves Twitch emote PNGs through the service so a panel with no route to the
/// internet (Q-series, USB reverse tunnel) can render them, and so the panel's
/// CSP needs no third-party image host. Empty bytes on any miss.
/// </summary>
public interface ITwitchEmoteCache
{
    Task<byte[]> GetEmoteAsync(string emoteId);
}

public sealed class TwitchEmoteCache : ITwitchEmoteCache
{
    private const string DefaultBaseUrl = "https://static-cdn.jtvnw.net";
    /// <summary>2.0 is the 56px variant - enough for a chat line, a quarter the bytes of 3.0.</summary>
    private const string Variant = "static/dark/2.0";
    private const int CacheCap = 256;
    private const long MaxEmoteBytes = 1_000_000;

    private static readonly TimeSpan MissTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _http;
    private readonly string _baseUrl;

    private readonly object _lock = new();
    private readonly Dictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _missExpiries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<byte[]>> _inFlight = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();

    public TwitchEmoteCache(IHttpClientFactory http)
        : this(http, DefaultBaseUrl)
    {
    }

    internal TwitchEmoteCache(IHttpClientFactory http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public Task<byte[]> GetEmoteAsync(string emoteId)
    {
        if (!TwitchChatMessageFactory.IsValidEmoteId(emoteId))
        {
            return Task.FromResult(Array.Empty<byte>());
        }

        lock (_lock)
        {
            if (_cache.TryGetValue(emoteId, out var cached))
            {
                Touch(emoteId);
                return Task.FromResult(cached);
            }
            if (_missExpiries.TryGetValue(emoteId, out var until))
            {
                if (DateTime.UtcNow < until)
                {
                    return Task.FromResult(Array.Empty<byte>());
                }
                _missExpiries.Remove(emoteId);
            }
            if (_inFlight.TryGetValue(emoteId, out var pending))
            {
                return pending;
            }

            // Shared so a chat burst repeating one emote issues a single fetch;
            // the request is bounded by the client timeout rather than by any
            // one caller's token, so an abandoned request cannot cancel it for
            // everyone else waiting on the same emote.
            var task = FetchAsync(emoteId);
            _inFlight[emoteId] = task;
            return task;
        }
    }

    private async Task<byte[]> FetchAsync(string emoteId)
    {
        byte[] bytes = Array.Empty<byte>();
        try
        {
            using var client = _http.CreateClient();
            client.Timeout = RequestTimeout;
            var url = $"{_baseUrl}/emoticons/v2/{emoteId}/{Variant}";
            using var response = await client.GetAsync(url).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK &&
                (response.Content.Headers.ContentLength ?? 0) <= MaxEmoteBytes)
            {
                bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.Length > MaxEmoteBytes)
                {
                    bytes = Array.Empty<byte>();
                }
            }
        }
        catch
        {
            bytes = Array.Empty<byte>();
        }

        lock (_lock)
        {
            _inFlight.Remove(emoteId);
            if (bytes.Length > 0)
            {
                _cache[emoteId] = bytes;
                Touch(emoteId);
                Evict();
            }
            else
            {
                _missExpiries[emoteId] = DateTime.UtcNow + MissTtl;
            }
        }
        return bytes;
    }

    private void Touch(string emoteId)
    {
        _lru.Remove(emoteId);
        _lru.AddFirst(emoteId);
    }

    private void Evict()
    {
        while (_lru.Count > CacheCap)
        {
            var oldest = _lru.Last;
            if (oldest is null)
            {
                return;
            }
            _lru.RemoveLast();
            _cache.Remove(oldest.Value);
        }
    }
}
