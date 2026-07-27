using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Models.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

/// <summary>
/// Exercises AlbumArtHdResolver's real HTTP path against a local raw TCP
/// listener (same approach as YahooStockQuoteProviderTests) so these run
/// fully offline - no live iTunes request.
/// </summary>
public sealed class AlbumArtHdResolverTests
{
    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static readonly byte[] JpegBytes = { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02, 0x03 };
    private static readonly byte[] OtherJpegBytes = { 0xFF, 0xD8, 0xFF, 0xE0, 0x09, 0x08, 0x07 };

    private static byte[] HttpOk(string jsonBody)
    {
        var body = Encoding.UTF8.GetBytes(jsonBody);
        var header = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/javascript; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        return Concat(header, body);
    }

    private static byte[] HttpImage(byte[] body)
    {
        var header = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: image/jpeg\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        return Concat(header, body);
    }

    private static byte[] HttpStatus(int code, string reason)
    {
        return Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {code} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }

    private static string ResultJson(string artist, string? collection, string? track, string artworkUrl)
    {
        var collectionJson = collection is null ? "" : $"\"collectionName\":\"{collection}\",";
        var trackJson = track is null ? "" : $"\"trackName\":\"{track}\",";
        return $"{{\"artistName\":\"{artist}\",{collectionJson}{trackJson}\"artworkUrl100\":\"{artworkUrl}\"}}";
    }

    private static string SearchJson(params string[] resultJsons)
    {
        return $"{{\"resultCount\":{resultJsons.Length},\"results\":[{string.Join(",", resultJsons)}]}}";
    }

    private const string EmptySearchJson = "{\"resultCount\":0,\"results\":[]}";

    /// <summary>Accepts connections in a background loop and replies with whatever ResponseFactory returns for that request line.</summary>
    private sealed class StubItunesServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly object _requestLock = new();
        private readonly List<string> _requestLines = new();

        public int Port { get; }
        public Func<string, byte[]> ResponseFactory { get; set; } = _ => HttpStatus(404, "Not Found");

        public int RequestCount
        {
            get { lock (_requestLock) { return _requestLines.Count; } }
        }

        public IReadOnlyList<string> RequestLines
        {
            get { lock (_requestLock) { return _requestLines.ToArray(); } }
        }

        public StubItunesServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl => $"http://127.0.0.1:{Port}";

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
                var response = ResponseFactory(requestLine);
                await stream.WriteAsync(response).ConfigureAwait(false);
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

    private static AlbumArtHdResolver NewResolver(
        StubItunesServer server, TimeSpan? negativeTtl = null, TimeSpan? failureTtl = null)
        => new(new SingleClientFactory(), server.BaseUrl, negativeTtl ?? TimeSpan.FromHours(1), failureTtl);

    private static MediaSong Song(string title, string artist, string album)
        => new() { Title = title, Artist = artist, Album = album };

    // ---- pure helpers ----

    [Theory]
    [InlineData("Song - 2011 Remaster", "Song")]
    [InlineData("Currents (Deluxe Edition)", "Currents")]
    [InlineData("1989 (Taylor's Version)", "1989")]
    [InlineData("Song (feat. Someone)", "Song")]
    [InlineData("Song feat. Someone", "Song")]
    [InlineData("Song - Live at Wembley", "Song")]
    [InlineData("Wish You Were Here", "Wish You Were Here")]
    [InlineData("Am I Ever Gonna See Your Face Again - The Best Of", "Am I Ever Gonna See Your Face Again - The Best Of")]
    public void Strip_decorations(string input, string expected)
    {
        Assert.Equal(expected, AlbumArtHdResolver.StripDecorations(input));
    }

    [Theory]
    [InlineData("Tame Impala", "Tame Impala", true)]
    [InlineData("Artist A, Artist B", "Artist A", true)]
    [InlineData("Beyoncé", "Beyonce", true)]
    [InlineData("Radiohead", "Coldplay", false)]
    [InlineData("", "Coldplay", false)]
    [InlineData("Radiohead", null, false)]
    public void Tokens_match(string expected, string? actual, bool want)
    {
        Assert.Equal(want, AlbumArtHdResolver.TokensMatch(expected, actual));
    }

    [Fact]
    public void Rewrite_artwork_size_replaces_trailing_dimension()
    {
        Assert.Equal(
            "https://example.com/img/1200x1200bb.jpg",
            AlbumArtHdResolver.RewriteArtworkSize("https://example.com/img/100x100bb.jpg", "1200x1200"));
        Assert.Null(AlbumArtHdResolver.RewriteArtworkSize("https://example.com/img/cover.jpg", "1200x1200"));
    }

    [Theory]
    [InlineData("Currents", "Currents", true)]
    [InlineData("Currents", "Currents B-Sides & Remixes - EP", false)]
    [InlineData("Currents", "Currents (Deluxe Edition)", true)]
    [InlineData("1989 (Taylor's Version)", "1989", true)]
    [InlineData("The Dark Side of the Moon", "Dark Side of the Moon", true)]
    [InlineData("Currents", null, false)]
    public void Name_matches(string expected, string? actual, bool want)
    {
        Assert.Equal(want, AlbumArtHdResolver.NameMatches(expected, actual));
    }

    [Fact]
    public void Build_attempts_orders_title_before_album_and_dedupes()
    {
        var attempts = AlbumArtHdResolver.BuildAttempts("Tame Impala", "Currents (Deluxe Edition)", "Let It Happen");
        Assert.Equal(
            new[]
            {
                ("Tame Impala Let It Happen", false),
                ("Tame Impala Currents (Deluxe Edition)", true),
                ("Tame Impala Currents", true),
            },
            attempts.ToArray());
    }

    // ---- HTTP path ----

    [Fact]
    public async Task Song_search_hit_downloads_hd_art_and_caches()
    {
        using var server = new StubItunesServer();
        server.ResponseFactory = line =>
            line.Contains("entity=song") ? HttpOk(SearchJson(
                ResultJson("Tame Impala", "Currents", "Let It Happen", $"{server.BaseUrl}/a/100x100bb.jpg")))
            : line.Contains("/a/1200x1200bb.jpg") ? HttpImage(JpegBytes)
            : HttpStatus(404, "Not Found");
        var resolver = NewResolver(server);

        var bytes = await resolver.GetHdAlbumArtAsync(Song("Let It Happen", "Tame Impala", "Currents"));
        Assert.Equal(JpegBytes, bytes);

        var countAfterFirst = server.RequestCount;
        var again = await resolver.GetHdAlbumArtAsync(Song("Let It Happen", "Tame Impala", "Currents"));
        Assert.Equal(JpegBytes, again);
        Assert.Equal(countAfterFirst, server.RequestCount);
    }

    [Fact]
    public async Task Song_result_with_matching_album_wins_over_earlier_compilation()
    {
        using var server = new StubItunesServer();
        server.ResponseFactory = line =>
            line.Contains("entity=song") ? HttpOk(SearchJson(
                ResultJson("Tame Impala", "Chill 2010s", "Let It Happen", $"{server.BaseUrl}/compilation/100x100bb.jpg"),
                ResultJson("Tame Impala", "Currents", "Let It Happen", $"{server.BaseUrl}/album/100x100bb.jpg")))
            : line.Contains("/album/1200x1200bb.jpg") ? HttpImage(JpegBytes)
            : HttpStatus(404, "Not Found");
        var resolver = NewResolver(server);

        var bytes = await resolver.GetHdAlbumArtAsync(Song("Let It Happen", "Tame Impala", "Currents"));

        Assert.Equal(JpegBytes, bytes);
    }

    [Fact]
    public async Task Album_entity_fallback_when_song_search_is_empty()
    {
        using var server = new StubItunesServer();
        server.ResponseFactory = line =>
            line.Contains("entity=song") ? HttpOk(EmptySearchJson)
            : line.Contains("entity=album") ? HttpOk(SearchJson(
                ResultJson("Tame Impala", "Currents", null, $"{server.BaseUrl}/a/100x100bb.jpg")))
            : line.Contains("/a/1200x1200bb.jpg") ? HttpImage(JpegBytes)
            : HttpStatus(404, "Not Found");
        var resolver = NewResolver(server);

        var bytes = await resolver.GetHdAlbumArtAsync(Song("Let It Happen", "Tame Impala", "Currents"));

        Assert.Equal(JpegBytes, bytes);
    }

    [Fact]
    public async Task Remix_ep_result_does_not_satisfy_album_search()
    {
        using var server = new StubItunesServer();
        server.ResponseFactory = line =>
            line.Contains("entity=song") ? HttpOk(EmptySearchJson)
            : line.Contains("entity=album") ? HttpOk(SearchJson(
                ResultJson("Tame Impala", "Currents B-Sides & Remixes - EP", null, $"{server.BaseUrl}/bsides/100x100bb.jpg")))
            : HttpStatus(404, "Not Found");
        var resolver = NewResolver(server);

        var bytes = await resolver.GetHdAlbumArtAsync(Song("Let It Happen", "Tame Impala", "Currents"));

        Assert.Empty(bytes);
    }

    [Fact]
    public async Task Album_attempt_outranks_song_fallback_on_wrong_release()
    {
        using var server = new StubItunesServer();
        server.ResponseFactory = line =>
            line.Contains("entity=song") ? HttpOk(SearchJson(
                ResultJson("Tame Impala", "Chill 2010s", "Let It Happen", $"{server.BaseUrl}/comp/100x100bb.jpg")))
            : line.Contains("entity=album") ? HttpOk(SearchJson(
                ResultJson("Tame Impala", "Currents", null, $"{server.BaseUrl}/album/100x100bb.jpg")))
            : line.Contains("/album/1200x1200bb.jpg") ? HttpImage(JpegBytes)
            : line.Contains("/comp/1200x1200bb.jpg") ? HttpImage(OtherJpegBytes)
            : HttpStatus(404, "Not Found");
        var resolver = NewResolver(server);

        var bytes = await resolver.GetHdAlbumArtAsync(Song("Let It Happen", "Tame Impala", "Currents"));

        Assert.Equal(JpegBytes, bytes);
    }

    [Fact]
    public async Task Song_fallback_serves_when_every_attempt_misses()
    {
        using var server = new StubItunesServer();
        server.ResponseFactory = line =>
            line.Contains("entity=song") ? HttpOk(SearchJson(
                ResultJson("Tame Impala", "Chill 2010s", "Let It Happen", $"{server.BaseUrl}/comp/100x100bb.jpg")))
            : line.Contains("entity=album") ? HttpOk(EmptySearchJson)
            : line.Contains("/comp/1200x1200bb.jpg") ? HttpImage(OtherJpegBytes)
            : HttpStatus(404, "Not Found");
        var resolver = NewResolver(server);

        var bytes = await resolver.GetHdAlbumArtAsync(Song("Let It Happen", "Tame Impala", "Currents"));

        Assert.Equal(OtherJpegBytes, bytes);
    }

    [Fact]
    public async Task Upstream_failure_negative_caches_briefly_not_for_the_full_ttl()
    {
        using var server = new StubItunesServer();
        server.ResponseFactory = _ => HttpStatus(500, "Internal Server Error");
        var resolver = NewResolver(server, negativeTtl: TimeSpan.FromHours(1), failureTtl: TimeSpan.Zero);

        var bytes = await resolver.GetHdAlbumArtAsync(Song("Let It Happen", "Tame Impala", "Currents"));
        Assert.Empty(bytes);

        server.ResponseFactory = line =>
            line.Contains("entity=song") ? HttpOk(SearchJson(
                ResultJson("Tame Impala", "Currents", "Let It Happen", $"{server.BaseUrl}/a/100x100bb.jpg")))
            : line.Contains("/a/1200x1200bb.jpg") ? HttpImage(JpegBytes)
            : HttpStatus(404, "Not Found");

        var retried = await resolver.GetHdAlbumArtAsync(Song("Let It Happen", "Tame Impala", "Currents"));
        Assert.Equal(JpegBytes, retried);
    }

    [Fact]
    public async Task Wrong_artist_fails_verification_and_negative_caches()
    {
        using var server = new StubItunesServer();
        server.ResponseFactory = line =>
            line.Contains("/search") ? HttpOk(SearchJson(
                ResultJson("Somebody Else", "Currents", "Let It Happen", $"{server.BaseUrl}/a/100x100bb.jpg")))
            : HttpStatus(404, "Not Found");
        var resolver = NewResolver(server);

        var bytes = await resolver.GetHdAlbumArtAsync(Song("Let It Happen", "Tame Impala", "Currents"));
        Assert.Empty(bytes);

        var countAfterFirst = server.RequestCount;
        var again = await resolver.GetHdAlbumArtAsync(Song("Let It Happen", "Tame Impala", "Currents"));
        Assert.Empty(again);
        Assert.Equal(countAfterFirst, server.RequestCount);
    }

    [Fact]
    public async Task Hd_download_failure_falls_back_to_600()
    {
        using var server = new StubItunesServer();
        server.ResponseFactory = line =>
            line.Contains("entity=song") ? HttpOk(SearchJson(
                ResultJson("Tame Impala", "Currents", "Let It Happen", $"{server.BaseUrl}/a/100x100bb.jpg")))
            : line.Contains("/a/1200x1200bb.jpg") ? HttpStatus(404, "Not Found")
            : line.Contains("/a/600x600bb.jpg") ? HttpImage(JpegBytes)
            : HttpStatus(404, "Not Found");
        var resolver = NewResolver(server);

        var bytes = await resolver.GetHdAlbumArtAsync(Song("Let It Happen", "Tame Impala", "Currents"));

        Assert.Equal(JpegBytes, bytes);
    }

    [Fact]
    public async Task Upstream_error_returns_empty()
    {
        using var server = new StubItunesServer();
        server.ResponseFactory = _ => HttpStatus(500, "Internal Server Error");
        var resolver = NewResolver(server);

        var bytes = await resolver.GetHdAlbumArtAsync(Song("Let It Happen", "Tame Impala", "Currents"));

        Assert.Empty(bytes);
    }

    [Fact]
    public async Task Missing_artist_skips_lookup_entirely()
    {
        using var server = new StubItunesServer();
        var resolver = NewResolver(server);

        var bytes = await resolver.GetHdAlbumArtAsync(Song("Some YouTube Video", "", ""));

        Assert.Empty(bytes);
        Assert.Equal(0, server.RequestCount);
    }
}
