using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Nexus.Service.Benchmarks.Kernels;

/// <summary>
/// Portable storage probe, the stand-in for the bundled DiskSpd (Windows-only,
/// and every permissively licensed cross-platform equivalent is GPL).
///
/// Writes, not reads: a read pass reports page-cache speed for any file that
/// fits in RAM, and both bypasses are out of reach - <c>fcntl(F_NOCACHE)</c> is
/// variadic, which a fixed-signature P/Invoke gets wrong on Apple arm64 where
/// variadic arguments go on the stack, and <c>O_DIRECT</c> needs aligned
/// buffers through a hand-rolled <c>open</c>.
///
/// Sequential times the flush with the writes, so the page cache cannot absorb
/// the file and the figure is bandwidth. Random 4K uses
/// <see cref="FileOptions.WriteThrough"/> (O_SYNC on Unix) so each operation
/// stands alone, giving queue-depth-1 IOPS and latency. On macOS neither flush
/// reaches the drive's own write cache - that needs F_FULLFSYNC, variadic
/// again - so both figures include it.
/// </summary>
internal static class StorageKernel
{
    internal const int SequentialBlockBytes = 1024 * 1024;
    internal const long SequentialTotalBytes = 512L * 1024 * 1024;
    internal const int RandomBlockBytes = 4096;
    internal const int RandomOpCount = 2048;
    internal const int SequentialTrials = 3;

    /// <summary>Caps a pass on a slow device; the rate uses the operations actually completed.</summary>
    private static readonly TimeSpan PassTimeCap = TimeSpan.FromSeconds(15);

    /// <summary>Fixed seed so every machine draws the same offset sequence.</summary>
    private const int RandomSeed = 0x4E455855;

    internal readonly record struct StorageMeasurement(
        double[] SequentialTrialsMbPerSec, double RandomIops, double AverageLatencyMs);

    internal static StorageMeasurement Measure(
        string path, Action<double>? onProgress, CancellationToken ct,
        long sequentialTotalBytes = SequentialTotalBytes,
        int randomOpCount = RandomOpCount,
        int sequentialTrials = SequentialTrials)
    {
        var trials = new double[sequentialTrials];
        for (int i = 0; i < sequentialTrials; i++)
        {
            double start = 0.7 * i / sequentialTrials;
            double span = 0.7 / sequentialTrials;
            trials[i] = MeasureSequential(
                path, sequentialTotalBytes, p => onProgress?.Invoke(start + span * p), ct);
        }
        var (iops, latencyMs) = MeasureRandom(path, randomOpCount, onProgress, ct);
        return new StorageMeasurement(trials, iops, latencyMs);
    }

    private static double MeasureSequential(
        string path, long totalBytes, Action<double>? onProgress, CancellationToken ct)
    {
        var block = new byte[SequentialBlockBytes];
        FillIncompressible(block, RandomSeed);

        using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 0, FileOptions.None);

        long written = 0;
        var sw = Stopwatch.StartNew();
        while (written < totalBytes && sw.Elapsed < PassTimeCap)
        {
            ct.ThrowIfCancellationRequested();
            stream.Write(block, 0, block.Length);
            written += block.Length;
            onProgress?.Invoke((double)written / totalBytes);
        }
        // Timed with the writes: they land in the page cache, so the rate only
        // reflects the device once the flush has drained it.
        stream.Flush(flushToDisk: true);
        sw.Stop();

        double seconds = sw.Elapsed.TotalSeconds;
        return seconds > 0 ? written / seconds / (1024d * 1024) : 0;
    }

    private static (double iops, double averageLatencyMs) MeasureRandom(
        string path, int opCount, Action<double>? onProgress, CancellationToken ct)
    {
        var block = new byte[RandomBlockBytes];
        FillIncompressible(block, RandomSeed + 1);

        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Write, FileShare.None,
            bufferSize: 0, FileOptions.WriteThrough);

        long length = stream.Length;
        if (length < RandomBlockBytes)
        {
            return (0, 0);
        }

        long blocks = length / RandomBlockBytes;
        var rng = new Random(RandomSeed);
        var sw = Stopwatch.StartNew();
        int completed = 0;
        for (int i = 0; i < opCount && sw.Elapsed < PassTimeCap; i++)
        {
            ct.ThrowIfCancellationRequested();
            stream.Seek(rng.NextInt64(blocks) * RandomBlockBytes, SeekOrigin.Begin);
            stream.Write(block, 0, block.Length);
            completed++;
            onProgress?.Invoke(0.7 + 0.3 * (double)completed / opCount);
        }
        stream.Flush(flushToDisk: true);
        sw.Stop();

        double seconds = sw.Elapsed.TotalSeconds;
        if (completed == 0 || seconds <= 0)
        {
            return (0, 0);
        }
        return (completed / seconds, seconds * 1000d / completed);
    }

    /// <summary>
    /// Pseudo-random bytes: a drive or filesystem with inline compression would
    /// otherwise collapse a zero-filled block and report a fictional rate.
    /// Mirrors DiskSpd's <c>-Zr</c> on the Windows path.
    /// </summary>
    private static void FillIncompressible(byte[] buffer, int seed)
    {
        new Random(seed).NextBytes(buffer);
    }
}
