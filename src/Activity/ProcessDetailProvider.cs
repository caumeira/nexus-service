using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Activity;

/// <summary>File-derived detail for GET /monitoring/process-info. Every
/// field is independently best-effort - a failure resolving one (e.g. no
/// VERSIONINFO resource) degrades that field to null rather than the whole
/// response.</summary>
public sealed record ProcessFileDetail(
    string? Description,
    string? Version,
    string? Company,
    string Signed,
    string? Publisher,
    long? CreatedAtMs,
    long? ModifiedAtMs);

/// <summary>Resolves an executable's file-derived detail (version info,
/// Authenticode signature, file times, content hash) for the monitoring
/// sidebar's process detail panel. Kept behind an interface so route tests
/// can substitute canned detail instead of depending on real files on the
/// test host's disk.</summary>
public interface IProcessDetailProvider
{
    ProcessFileDetail GetFileDetail(string path);

    /// <summary>Null when the file could not be read, or exceeds the size
    /// cap (hashing it would stall the request for multiple seconds).
    /// Cached by (path, mtime) - a repeat request for the same unchanged
    /// file never re-hashes it.</summary>
    Task<string?> ComputeSha256Async(string path, CancellationToken ct);
}

public sealed class ProcessDetailProvider : IProcessDetailProvider
{
    // Hashing a file this large would block the request for multiple
    // seconds; skip it rather than stall, matching the wire's nullable
    // sha256 field.
    private const long MaxHashableBytes = 200L * 1024 * 1024;
    private const int HashStreamBufferSize = 81920;

    private readonly ProcessHashCache _hashCache;

    // Dedupes concurrent first-time hashes of the same (path, mtime): several
    // process-info requests for the same uncached exe arriving together share
    // one hash pass instead of each reading and hashing the file separately.
    // Same shape as ExternalToolManager's in-flight resolve map.
    private readonly ConcurrentDictionary<(string Path, long MtimeTicks), Task<string?>> _inFlightHashes = new();

    public ProcessDetailProvider(ProcessHashCache hashCache) { _hashCache = hashCache; }

    public ProcessFileDetail GetFileDetail(string path)
    {
        string? description = null;
        string? version = null;
        string? company = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            description = NullIfEmpty(info.FileDescription);
            version = NullIfEmpty(info.FileVersion);
            company = NullIfEmpty(info.CompanyName);
        }
        catch { /* no VERSIONINFO resource, or unreadable - leave null */ }

        var (signed, publisher) = ProcessSignatureChecker.Check(path);

        long? createdAtMs = null;
        long? modifiedAtMs = null;
        try
        {
            // FileInfo does not validate existence at construction, and its
            // time properties silently return the FILETIME epoch (not throw)
            // for a missing file - check Exists first, or a missing path
            // "resolves" to a bogus 1601 timestamp instead of null.
            var fi = new FileInfo(path);
            if (fi.Exists)
            {
                createdAtMs = new DateTimeOffset(fi.CreationTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
                modifiedAtMs = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
            }
        }
        catch { /* leave null */ }

        return new ProcessFileDetail(description, version, company, signed, publisher, createdAtMs, modifiedAtMs);
    }

    public async Task<string?> ComputeSha256Async(string path, CancellationToken ct)
    {
        long mtimeTicks;
        long length;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
            {
                return null;
            }
            mtimeTicks = fi.LastWriteTimeUtc.Ticks;
            length = fi.Length;
        }
        catch
        {
            return null;
        }

        if (_hashCache.TryGet(path, mtimeTicks, out var cached))
        {
            return cached;
        }
        if (length > MaxHashableBytes)
        {
            return null;
        }

        var key = (path, mtimeTicks);
        var task = _inFlightHashes.GetOrAdd(key, _ => HashAndCacheAsync(path, mtimeTicks, ct));
        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            _inFlightHashes.TryRemove(key, out _);
        }
    }

    private async Task<string?> HashAndCacheAsync(string path, long mtimeTicks, CancellationToken ct)
    {
        string hash;
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, HashStreamBufferSize, useAsync: true);
            var bytes = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            hash = Convert.ToHexString(bytes).ToLowerInvariant();
        }
        catch
        {
            return null;
        }

        _hashCache.Set(path, mtimeTicks, hash);
        return hash;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
