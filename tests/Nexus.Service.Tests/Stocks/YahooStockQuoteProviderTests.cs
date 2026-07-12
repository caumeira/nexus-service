using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform.Stocks;
using Xunit;

namespace Nexus.Service.Tests.Stocks;

/// <summary>
/// Exercises YahooStockQuoteProvider's real HTTP path against a local raw
/// TCP listener (same approach as CloudApiClientTests) so these run fully
/// offline - no mocking library, no live Yahoo request.
/// </summary>
public sealed class YahooStockQuoteProviderTests
{
    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private const string AaplChartJson =
        "{\"chart\":{\"result\":[{\"meta\":{\"shortName\":\"Apple Inc.\",\"regularMarketPrice\":213.25," +
        "\"chartPreviousClose\":210.5,\"priceHint\":2},\"indicators\":{\"quote\":[{\"close\":[210.5,null,213.25]}]}}]," +
        "\"error\":null}}";

    private static string HttpOk(string jsonBody)
    {
        var byteCount = Encoding.UTF8.GetByteCount(jsonBody);
        return $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {byteCount}\r\nConnection: close\r\n\r\n{jsonBody}";
    }

    private static string HttpStatus(int code, string reason)
    {
        return $"HTTP/1.1 {code} {reason}\r\nContent-Type: application/json\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
    }

    /// <summary>Accepts connections in a background loop and replies with whatever ResponseFactory returns for that request line.</summary>
    private sealed class StubYahooServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly object _requestLock = new();
        private readonly List<string> _requestLines = new();
        private int _requestCount;

        public int Port { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);
        public Func<string, string> ResponseFactory { get; set; } = _ => HttpOk("{}");

        public StubYahooServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var requestLine = await ReadRequestLineAsync(stream).ConfigureAwait(false);
                lock (_requestLock)
                {
                    _requestLines.Add(requestLine);
                }
                Interlocked.Increment(ref _requestCount);

                var response = ResponseFactory(requestLine);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response)).ConfigureAwait(false);
            }
        }

        private static async Task<string> ReadRequestLineAsync(NetworkStream stream)
        {
            var buffer = new byte[8192];
            var text = new StringBuilder();
            while (!text.ToString().Contains("\r\n\r\n"))
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            var full = text.ToString();
            var idx = full.IndexOf("\r\n", StringComparison.Ordinal);
            return idx >= 0 ? full[..idx] : full;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _acceptLoop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task Happy_path_parses_price_change_and_drops_null_closes_from_series()
    {
        using var server = new StubYahooServer { ResponseFactory = _ => HttpOk(AaplChartJson) };
        var provider = new YahooStockQuoteProvider(new SingleClientFactory(), $"http://127.0.0.1:{server.Port}");

        var response = await provider.GetQuotesAsync(new[] { "AAPL" }, "1d");

        var quote = Assert.Single(response.Quotes);
        Assert.Equal("AAPL", quote.Symbol);
        Assert.Equal("Apple Inc.", quote.Name);
        Assert.Equal(213.25, quote.Price);
        Assert.Equal(2.75, quote.Change!.Value, 3);
        Assert.Equal(1.306, quote.ChangePercent!.Value, 3);
        Assert.Equal(2, quote.PriceHint);
        Assert.Equal(new[] { 210.5, 213.25 }, quote.Series);
    }

    [Fact]
    public async Task Cache_hit_within_ttl_does_not_refetch_upstream()
    {
        using var server = new StubYahooServer { ResponseFactory = _ => HttpOk(AaplChartJson) };
        var provider = new YahooStockQuoteProvider(new SingleClientFactory(), $"http://127.0.0.1:{server.Port}");

        await provider.GetQuotesAsync(new[] { "AAPL" }, "1d");
        await provider.GetQuotesAsync(new[] { "AAPL" }, "1d");

        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task Upstream_failure_with_warm_cache_serves_stale_quote()
    {
        using var server = new StubYahooServer { ResponseFactory = _ => HttpOk(AaplChartJson) };
        var provider = new YahooStockQuoteProvider(
            new SingleClientFactory(), $"http://127.0.0.1:{server.Port}",
            dailyTtl: TimeSpan.FromMilliseconds(20), otherRangeTtl: TimeSpan.FromMilliseconds(20), staleServeTtl: TimeSpan.FromHours(2));

        var first = await provider.GetQuotesAsync(new[] { "AAPL" }, "1d");
        Assert.Equal(213.25, first.Quotes[0].Price);

        // Let the provider's short TTL lapse so the next call treats the
        // entry as expired and attempts a refetch, which the reconfigured
        // stub then fails - exercising the stale-serve fallback rather than
        // a fresh cache hit.
        await Task.Delay(60);
        server.ResponseFactory = _ => HttpStatus(500, "Internal Server Error");

        var second = await provider.GetQuotesAsync(new[] { "AAPL" }, "1d");

        Assert.Equal(213.25, second.Quotes[0].Price);
        Assert.Equal("Apple Inc.", second.Quotes[0].Name);
        Assert.True(server.RequestCount >= 2);
    }

    [Fact]
    public async Task Upstream_failure_with_no_cache_returns_symbol_only_quote()
    {
        using var server = new StubYahooServer { ResponseFactory = _ => HttpStatus(500, "Internal Server Error") };
        var provider = new YahooStockQuoteProvider(new SingleClientFactory(), $"http://127.0.0.1:{server.Port}");

        var response = await provider.GetQuotesAsync(new[] { "AAPL" }, "1d");

        var quote = Assert.Single(response.Quotes);
        Assert.Equal("AAPL", quote.Symbol);
        Assert.Null(quote.Name);
        Assert.Null(quote.Price);
        Assert.Null(quote.Change);
        Assert.Null(quote.ChangePercent);
        Assert.Null(quote.PriceHint);
        Assert.Empty(quote.Series);
    }
}
