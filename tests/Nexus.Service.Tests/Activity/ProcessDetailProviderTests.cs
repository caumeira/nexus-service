using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class ProcessDetailProviderTests : IDisposable
{
    private readonly string _dir;

    public ProcessDetailProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-processdetail-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteFile(string content)
    {
        var path = Path.Combine(_dir, "app.bin");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void GetFileDetail_ReadsRealFileTimes_ForAnOrdinaryFile()
    {
        var path = WriteFile("not a pe file");
        var provider = new ProcessDetailProvider(new ProcessHashCache());

        var detail = provider.GetFileDetail(path);

        Assert.NotNull(detail.CreatedAtMs);
        Assert.NotNull(detail.ModifiedAtMs);
        // Not a real PE file: FileVersionInfo has nothing to report.
        Assert.Null(detail.Description);
        Assert.Null(detail.Version);
        Assert.Null(detail.Company);
    }

    [Fact]
    public void GetFileDetail_ReturnsNulls_WhenTheFileDoesNotExist()
    {
        var provider = new ProcessDetailProvider(new ProcessHashCache());

        var detail = provider.GetFileDetail(Path.Combine(_dir, "missing.bin"));

        Assert.Null(detail.CreatedAtMs);
        Assert.Null(detail.ModifiedAtMs);
    }

    [Fact]
    public async Task ComputeSha256Async_ReturnsTheRealDigest_OfTheFileContent()
    {
        var path = WriteFile("hello world");
        var provider = new ProcessDetailProvider(new ProcessHashCache());
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hello world"))).ToLowerInvariant();

        var hash = await provider.ComputeSha256Async(path, CancellationToken.None);

        Assert.Equal(expected, hash);
    }

    [Fact]
    public async Task ComputeSha256Async_ReturnsNull_WhenTheFileDoesNotExist()
    {
        var provider = new ProcessDetailProvider(new ProcessHashCache());

        var hash = await provider.ComputeSha256Async(Path.Combine(_dir, "missing.bin"), CancellationToken.None);

        Assert.Null(hash);
    }

    [Fact]
    public async Task ComputeSha256Async_ServesTheCachedDigest_WhenMtimeIsUnchanged()
    {
        var path = WriteFile("hello world");
        var provider = new ProcessDetailProvider(new ProcessHashCache());
        var originalMtime = File.GetLastWriteTimeUtc(path);
        var firstHash = await provider.ComputeSha256Async(path, CancellationToken.None);

        // Overwrite the content but restore the original mtime: a cache
        // keyed purely by (path, mtime) must serve the stale cached digest
        // rather than recompute, proving the lookup - not a fresh hash of
        // the new bytes - is what answered the second call.
        File.WriteAllText(path, "completely different content");
        File.SetLastWriteTimeUtc(path, originalMtime);

        var secondHash = await provider.ComputeSha256Async(path, CancellationToken.None);

        Assert.Equal(firstHash, secondHash);
    }

    [Fact]
    public async Task ComputeSha256Async_RecomputesTheDigest_AfterTheFileIsRewritten()
    {
        var path = WriteFile("hello world");
        var provider = new ProcessDetailProvider(new ProcessHashCache());
        var firstHash = await provider.ComputeSha256Async(path, CancellationToken.None);

        // A real rewrite (mtime included) must not serve the stale digest.
        Thread.Sleep(10);
        File.WriteAllText(path, "hello world 2");

        var secondHash = await provider.ComputeSha256Async(path, CancellationToken.None);

        Assert.NotEqual(firstHash, secondHash);
    }

    [Fact]
    public async Task ComputeSha256Async_ReturnsNull_WhenTheFileExceedsTheSizeCap()
    {
        var path = Path.Combine(_dir, "huge.bin");
        using (var fs = new FileStream(path, FileMode.CreateNew))
        {
            fs.SetLength(200L * 1024 * 1024 + 1);
        }
        var provider = new ProcessDetailProvider(new ProcessHashCache());

        var hash = await provider.ComputeSha256Async(path, CancellationToken.None);

        Assert.Null(hash);
    }
}
