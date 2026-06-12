using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Common.ExternalTools;
using Xunit;

namespace Nexus.Service.Tests.Common.ExternalTools;

public class VerifiedDownloadTests : IDisposable
{
    private readonly string _tempRoot;

    public VerifiedDownloadTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "nexus-tooldl-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task DownloadAsync_writes_binary_when_hash_and_size_match()
    {
        var payload = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var http = new HttpClient(new FakeHandler { Response = OkBytes(payload) });
        var dest = Path.Combine(_tempRoot, "tool.exe");

        await VerifiedDownload.DownloadAsync(http, "https://assets.hellonexus.com/x/tool.exe", dest,
            Sha256Hex(payload), payload.Length, CancellationToken.None);

        Assert.True(File.Exists(dest));
        Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public async Task DownloadAsync_discards_on_sha_mismatch()
    {
        var payload = new byte[] { 0xDE, 0xAD };
        var http = new HttpClient(new FakeHandler { Response = OkBytes(payload) });
        var dest = Path.Combine(_tempRoot, "tool.exe");

        await Assert.ThrowsAsync<InvalidDataException>(() => VerifiedDownload.DownloadAsync(
            http, "https://assets.hellonexus.com/x/tool.exe", dest,
            expectedSha256: new string('0', 64), expectedSize: payload.Length, CancellationToken.None));

        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".tmp"));
    }

    [Fact]
    public async Task DownloadAsync_discards_on_size_mismatch()
    {
        var payload = new byte[] { 0x01, 0x02 };
        var http = new HttpClient(new FakeHandler { Response = OkBytes(payload) });
        var dest = Path.Combine(_tempRoot, "tool.exe");

        await Assert.ThrowsAsync<InvalidDataException>(() => VerifiedDownload.DownloadAsync(
            http, "https://assets.hellonexus.com/x/tool.exe", dest,
            Sha256Hex(payload), expectedSize: 999, CancellationToken.None));

        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task DownloadAsync_skips_network_when_cached_copy_is_valid()
    {
        var payload = new byte[] { 0xAA, 0xBB, 0xCC };
        var dest = Path.Combine(_tempRoot, "tool.exe");
        await File.WriteAllBytesAsync(dest, payload);

        var handler = new ExplodingHandler();
        var http = new HttpClient(handler);

        await VerifiedDownload.DownloadAsync(http, "https://assets.hellonexus.com/x/tool.exe", dest,
            Sha256Hex(payload), payload.Length, CancellationToken.None);

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task IsValidAsync_true_only_when_hash_matches()
    {
        var payload = new byte[] { 0x11, 0x22, 0x33 };
        var path = Path.Combine(_tempRoot, "tool.exe");
        await File.WriteAllBytesAsync(path, payload);

        Assert.True(await VerifiedDownload.IsValidAsync(path, Sha256Hex(payload), payload.Length, CancellationToken.None));
        Assert.False(await VerifiedDownload.IsValidAsync(path, new string('0', 64), payload.Length, CancellationToken.None));
        Assert.False(await VerifiedDownload.IsValidAsync(path, Sha256Hex(payload), 999, CancellationToken.None));
        Assert.False(await VerifiedDownload.IsValidAsync(Path.Combine(_tempRoot, "missing.exe"), Sha256Hex(payload), payload.Length, CancellationToken.None));
    }

    // ── Helpers ──

    private static HttpResponseMessage OkBytes(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Response);
    }

    private sealed class ExplodingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("should not hit the network for a cached, valid binary");
        }
    }
}
