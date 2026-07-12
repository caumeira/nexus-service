using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Stocks;

namespace Nexus.Service.Platform.Stocks;

/// <summary>
/// Stock/index quote provider backed by Yahoo Finance's undocumented chart
/// endpoint (no API key). Yahoo's chart endpoint is single-symbol only, so
/// <see cref="GetQuotesAsync"/> fans out one request per symbol concurrently.
///
/// The per-symbol cache lock is released before the HTTP fetch and
/// re-acquired only to read the snapshot and to write the result back -
/// unlike a lock held across the whole fetch, this lets concurrent symbols
/// in one request fetch in parallel instead of queuing behind a single
/// shared lock. A re-check on write-back avoids clobbering a fresher entry
/// another caller already wrote while this fetch was in flight.
/// </summary>
public sealed class YahooStockQuoteProvider : IStockQuoteProvider
{
    private const string DefaultBaseUrl = "https://query1.finance.yahoo.com";

    private static readonly TimeSpan DefaultDailyTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultOtherRangeTtl = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan DefaultStaleServeTtl = TimeSpan.FromHours(2);

    private readonly IHttpClientFactory _http;
    private readonly string _baseUrl;
    private readonly TimeSpan _dailyTtl;
    private readonly TimeSpan _otherRangeTtl;
    private readonly TimeSpan _staleServeTtl;

    private readonly Dictionary<(string Symbol, string Range), (StockQuote Quote, DateTime FetchedUtc)> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public YahooStockQuoteProvider(IHttpClientFactory http)
        : this(http, DefaultBaseUrl)
    {
    }

    // Test-only ctor: an explicit base URL so tests can point at a loopback
    // listener instead of the real Yahoo host.
    internal YahooStockQuoteProvider(IHttpClientFactory http, string baseUrl)
        : this(http, baseUrl, DefaultDailyTtl, DefaultOtherRangeTtl, DefaultStaleServeTtl)
    {
    }

    // Test-only ctor: overridable cache TTLs so a test can force an entry
    // past its primary TTL (and into the stale-serve window) without a real
    // multi-minute sleep.
    internal YahooStockQuoteProvider(
        IHttpClientFactory http, string baseUrl, TimeSpan dailyTtl, TimeSpan otherRangeTtl, TimeSpan staleServeTtl)
    {
        _http = http;
        _baseUrl = baseUrl;
        _dailyTtl = dailyTtl;
        _otherRangeTtl = otherRangeTtl;
        _staleServeTtl = staleServeTtl;
    }

    public async Task<StockQuotesResponse> GetQuotesAsync(IReadOnlyList<string> symbols, string range)
    {
        try
        {
            var tasks = new List<Task<StockQuote>>(symbols.Count);
            foreach (var symbol in symbols)
            {
                tasks.Add(GetOneAsync(symbol, range));
            }
            var quotes = await Task.WhenAll(tasks).ConfigureAwait(false);
            return new StockQuotesResponse { Range = range, Quotes = new List<StockQuote>(quotes) };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[stocks] failed: {ex.Message}");
            var quotes = new List<StockQuote>(symbols.Count);
            foreach (var symbol in symbols)
            {
                quotes.Add(new StockQuote { Symbol = symbol });
            }
            return new StockQuotesResponse { Range = range, Quotes = quotes };
        }
    }

    private async Task<StockQuote> GetOneAsync(string symbol, string range)
    {
        try
        {
            var key = (Symbol: symbol, Range: range);
            var ttl = range == "1d" ? _dailyTtl : _otherRangeTtl;

            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_cache.TryGetValue(key, out var cached) && (DateTime.UtcNow - cached.FetchedUtc) < ttl)
                {
                    return cached.Quote;
                }
            }
            finally
            {
                _lock.Release();
            }

            var fetched = await FetchQuoteAsync(symbol, range).ConfigureAwait(false);

            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var now = DateTime.UtcNow;
                if (fetched is not null)
                {
                    if (!_cache.TryGetValue(key, out var raced) || (now - raced.FetchedUtc) >= ttl)
                    {
                        _cache[key] = (fetched, now);
                    }
                    return fetched;
                }

                if (_cache.TryGetValue(key, out var stale) && (now - stale.FetchedUtc) < _staleServeTtl)
                {
                    return stale.Quote;
                }

                return new StockQuote { Symbol = symbol };
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[stocks] {symbol} failed: {ex.Message}");
            return new StockQuote { Symbol = symbol };
        }
    }

    private async Task<StockQuote?> FetchQuoteAsync(string symbol, string range)
    {
        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(6);
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", Nexus.Service.Widgets.AppProxyService.DefaultUserAgent);

            var interval = IntervalFor(range);
            var url = $"{_baseUrl}/v8/finance/chart/{Uri.EscapeDataString(symbol)}?range={range}&interval={interval}";
            var resp = await client.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[stocks] {symbol} returned {(int)resp.StatusCode}");
                return null;
            }

            var payload = await resp.Content.ReadFromJsonAsync(
                Nexus.Service.Serialization.AppJsonContext.Default.YahooChartResponse
            ).ConfigureAwait(false);

            if (payload?.Chart?.Error is not null)
            {
                Console.Error.WriteLine($"[stocks] {symbol} chart error: {payload.Chart.Error.Description}");
                return null;
            }

            var result = payload?.Chart?.Result is { Length: > 0 } results ? results[0] : null;
            var meta = result?.Meta;
            if (meta is null)
            {
                Console.Error.WriteLine($"[stocks] {symbol} missing chart meta");
                return null;
            }

            var price = meta.RegularMarketPrice;
            var prevClose = meta.ChartPreviousClose;
            double? change = null;
            double? changePercent = null;
            if (price is not null && prevClose is not null)
            {
                change = price.Value - prevClose.Value;
                changePercent = prevClose.Value == 0 ? null : (price.Value - prevClose.Value) / prevClose.Value * 100;
            }

            return new StockQuote
            {
                Symbol = symbol,
                Name = string.IsNullOrEmpty(meta.ShortName) ? symbol : meta.ShortName,
                Price = price,
                Change = change,
                ChangePercent = changePercent,
                PriceHint = meta.PriceHint,
                Series = BuildSeries(result),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[stocks] {symbol} fetch failed: {ex.Message}");
            return null;
        }
    }

    private static List<double> BuildSeries(YahooChartResult? result)
    {
        var closes = result?.Indicators?.Quote is { Length: > 0 } quotes ? quotes[0].Close : null;
        if (closes is null)
        {
            return new List<double>();
        }

        var series = new List<double>(closes.Length);
        foreach (var c in closes)
        {
            if (c.HasValue)
            {
                series.Add(c.Value);
            }
        }
        return series;
    }

    private static string IntervalFor(string range) => range switch
    {
        "5d" => "30m",
        "1mo" or "3mo" or "6mo" => "1d",
        "1y" => "1wk",
        _ => "5m",
    };
}

public sealed class YahooChartResponse
{
    [JsonPropertyName("chart")] public YahooChart? Chart { get; set; }
}

public sealed class YahooChart
{
    [JsonPropertyName("result")] public YahooChartResult[]? Result { get; set; }
    [JsonPropertyName("error")] public YahooChartError? Error { get; set; }
}

public sealed class YahooChartError
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
}

public sealed class YahooChartResult
{
    [JsonPropertyName("meta")] public YahooChartMeta? Meta { get; set; }
    [JsonPropertyName("indicators")] public YahooChartIndicators? Indicators { get; set; }
}

public sealed class YahooChartMeta
{
    [JsonPropertyName("shortName")] public string? ShortName { get; set; }
    [JsonPropertyName("regularMarketPrice")] public double? RegularMarketPrice { get; set; }
    [JsonPropertyName("chartPreviousClose")] public double? ChartPreviousClose { get; set; }
    [JsonPropertyName("priceHint")] public int? PriceHint { get; set; }
}

public sealed class YahooChartIndicators
{
    [JsonPropertyName("quote")] public YahooChartQuote[]? Quote { get; set; }
}

public sealed class YahooChartQuote
{
    [JsonPropertyName("close")] public double?[]? Close { get; set; }
}
