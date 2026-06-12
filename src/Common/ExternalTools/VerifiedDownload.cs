using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Common.ExternalTools;

/// <summary>
/// Atomic HTTP download + SHA-256 + size verification. Extracted from the firmware
/// store's <c>DownloadAsync</c> so the external-tool manager hash-pins the same way
/// without taking a dependency on the (unrelated) firmware subsystem. Both run the
/// identical primitive: stream to a <c>.tmp</c>, size-check, hash-check in place,
/// then rename onto the final path.
/// </summary>
public static class VerifiedDownload
{
    private const int CopyBufferSize = 81920;

    /// <summary>Lowercase hex SHA-256 of a file on disk.</summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// True when <paramref name="path"/> exists and matches the expected size (when
    /// &gt; 0) and SHA-256. Lets a caller skip a download when the cache is already
    /// valid, and re-verify a preloaded/bundled binary before trusting it.
    /// </summary>
    public static async Task<bool> IsValidAsync(string path, string expectedSha256, long expectedSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)) return false;
        if (!File.Exists(path)) return false;
        if (expectedSize > 0 && new FileInfo(path).Length != expectedSize) return false;
        var actual = await ComputeSha256Async(path, ct);
        return string.Equals(actual, expectedSha256.ToLowerInvariant(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Download <paramref name="url"/> to <paramref name="finalPath"/>, verifying
    /// size then SHA-256 before the binary is committed. Streams to
    /// <c>&lt;finalPath&gt;.tmp</c>; on any mismatch the temp file is deleted and an
    /// <see cref="InvalidDataException"/> is thrown so a tampered/corrupt binary is
    /// never moved into place. Returns immediately if a valid file is already cached.
    /// </summary>
    public static async Task DownloadAsync(
        HttpClient http,
        string url,
        string finalPath,
        string expectedSha256,
        long expectedSize,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("Download URL is required.", nameof(url));
        if (string.IsNullOrWhiteSpace(expectedSha256)) throw new ArgumentException("Expected SHA-256 is required.", nameof(expectedSha256));

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        if (await IsValidAsync(finalPath, expectedSha256, expectedSize, ct))
            return;

        var tmpPath = finalPath + ".tmp";
        TryDelete(tmpPath);

        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);
            await CopyAsync(src, dst, ct);
        }

        var actualSize = new FileInfo(tmpPath).Length;
        if (expectedSize > 0 && actualSize != expectedSize)
        {
            TryDelete(tmpPath);
            throw new InvalidDataException($"Downloaded {url} size {actualSize} does not match expected {expectedSize}.");
        }

        var actualSha = await ComputeSha256Async(tmpPath, ct);
        if (!string.Equals(actualSha, expectedSha256.ToLowerInvariant(), StringComparison.Ordinal))
        {
            TryDelete(tmpPath);
            throw new InvalidDataException($"Downloaded {url} SHA-256 {actualSha} does not match expected {expectedSha256.ToLowerInvariant()}.");
        }

        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(tmpPath, finalPath);
    }

    private static async Task CopyAsync(Stream src, Stream dst, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            int read;
            while ((read = await src.ReadAsync(buf.AsMemory(0, CopyBufferSize), ct)) > 0)
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
