using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Benchmarks;

namespace Nexus.Service.Benchmarks.Providers;

/// <summary>
/// Cross-platform benchmark provider. All four sub-benchmarks are pure managed
/// .NET workloads (AOT-safe, no native deps beyond the BCL).
///
/// CPU: SHA-256 hashing loop, single-core then multi-core. Exercises the
/// integer pipeline and, where available, AES-NI/SHA-NI extensions via the
/// BCL's HW-intrinsic IncrementalHash path.
///
/// RAM: Buffer.MemoryCopy bandwidth between two 256 MB arrays, followed by
/// random-64-byte reads from a 64 MB buffer. Bandwidth dominates the score;
/// latency is a minor tie-breaker.
///
/// Storage: writes 1 MB chunks to a temp file (capped at 4 GB), reads back
/// sequentially, then does 4K random reads. Temp file is removed after.
///
/// GPU: SIMD (System.Numerics.Vector&lt;float&gt;) matmul-style workload in
/// parallel across every logical core. This is a compute proxy, not a real
/// GPU test; the score slot + contract match a future OpenGL/OpenCL impl so
/// it can be swapped without breaking the public API.
/// </summary>
public sealed class DefaultBenchmarkProvider : IBenchmarkProvider
{
    private const int CpuSingleSeconds = 8;
    private const int CpuMultiSeconds = 12;
    private const int RamBandwidthSeconds = 4;
    private const int RamRandomSeconds = 3;
    private const int StorageSeqSeconds = 4;
    private const int StorageRandomSeconds = 4;
    private const int GpuSeconds = 10;

    public async Task<BenchmarkSubScore> RunCpuAsync(
        IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct)
    {
        try
        {
            // Single-core
            progress.Report(new BenchmarkPhaseProgress
            {
                Phase = "cpu",
                Detail = "single-core",
                Percent = 0,
            });
            var singleHashesPerSec = await Task.Run(
                () => HashLoopForSeconds(CpuSingleSeconds, ct,
                    (sec, current) => progress.Report(new BenchmarkPhaseProgress
                    {
                        Phase = "cpu",
                        Detail = "single-core",
                        Percent = sec / (double)(CpuSingleSeconds + CpuMultiSeconds),
                        CurrentRaw = current,
                        CurrentUnit = "hashes/s",
                    })),
                ct);

            // Multi-core
            progress.Report(new BenchmarkPhaseProgress
            {
                Phase = "cpu",
                Detail = "multi-core",
                Percent = CpuSingleSeconds / (double)(CpuSingleSeconds + CpuMultiSeconds),
            });
            int threads = Math.Max(1, Environment.ProcessorCount);
            double[] perThread = new double[threads];
            var tasks = new Task[threads];
            var swMulti = Stopwatch.StartNew();
            var endMulti = TimeSpan.FromSeconds(CpuMultiSeconds);
            for (int i = 0; i < threads; i++)
            {
                int idx = i;
                tasks[i] = Task.Run(() =>
                {
                    perThread[idx] = HashLoopForDuration(endMulti, ct, null);
                }, ct);
            }
            await Task.WhenAll(tasks);
            double multiHashesPerSec = 0;
            for (int i = 0; i < threads; i++)
                multiHashesPerSec += perThread[i];

            // Combined raw metric: geometric mean of single + multi. Keeps the
            // test fair between a fast-but-few-core chip and a slower many-core.
            double combined = Math.Sqrt(singleHashesPerSec * (multiHashesPerSec / Math.Max(1, threads))) * Math.Sqrt(threads);
            double score = Scoring.Normalize(combined, Scoring.RefCpuHashesPerSec);

            return new BenchmarkSubScore
            {
                Key = "cpu",
                Label = "CPU",
                Score = score,
                RawValue = Math.Round(multiHashesPerSec / 1_000_000d, 1),
                RawUnit = "Mhash/s",
                Detail = $"{threads} threads, {Math.Round(singleHashesPerSec / 1_000_000d, 1)} Mhash/s single",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new BenchmarkSubScore
            {
                Key = "cpu",
                Label = "CPU",
                Score = 0,
                Detail = $"failed: {ex.Message}",
            };
        }
    }

    private static double HashLoopForSeconds(int seconds, CancellationToken ct, Action<int, double>? onTick)
    {
        var end = TimeSpan.FromSeconds(seconds);
        var sw = Stopwatch.StartNew();
        var buffer = new byte[64 * 1024];
        new Random(42).NextBytes(buffer);
        long hashes = 0;
        int lastSec = -1;
        using var inc = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (sw.Elapsed < end)
        {
            ct.ThrowIfCancellationRequested();
            inc.AppendData(buffer);
            _ = inc.GetHashAndReset();
            hashes += buffer.Length;
            int s = (int)sw.Elapsed.TotalSeconds;
            if (s != lastSec)
            {
                lastSec = s;
                if (onTick is not null)
                {
                    double bytesPerSec = hashes / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    onTick(s, bytesPerSec);
                }
            }
        }
        return hashes / Math.Max(0.001, sw.Elapsed.TotalSeconds);
    }

    private static double HashLoopForDuration(TimeSpan duration, CancellationToken ct, Action<double>? onTick)
    {
        var sw = Stopwatch.StartNew();
        var buffer = new byte[64 * 1024];
        new Random(42).NextBytes(buffer);
        long hashes = 0;
        using var inc = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (sw.Elapsed < duration)
        {
            if (ct.IsCancellationRequested)
                break;
            inc.AppendData(buffer);
            _ = inc.GetHashAndReset();
            hashes += buffer.Length;
        }
        return hashes / Math.Max(0.001, sw.Elapsed.TotalSeconds);
    }

    public async Task<BenchmarkSubScore> RunRamAsync(
        IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct)
    {
        try
        {
            var ramResult = await Task.Run(() =>
            {
                // Bandwidth: repeatedly copy src -> dst, 256 MB each.
                const int bufSize = 256 * 1024 * 1024;
                var src = new byte[bufSize];
                var dst = new byte[bufSize];
                new Random(17).NextBytes(src);

                var sw = Stopwatch.StartNew();
                long bytesMoved = 0;
                var end = TimeSpan.FromSeconds(RamBandwidthSeconds);
                while (sw.Elapsed < end)
                {
                    ct.ThrowIfCancellationRequested();
                    Buffer.BlockCopy(src, 0, dst, 0, bufSize);
                    bytesMoved += bufSize;
                    double pct = Math.Min(1, sw.Elapsed.TotalSeconds / (RamBandwidthSeconds + RamRandomSeconds));
                    progress.Report(new BenchmarkPhaseProgress
                    {
                        Phase = "ram",
                        Detail = "bandwidth",
                        Percent = pct,
                        CurrentRaw = bytesMoved / Math.Max(0.001, sw.Elapsed.TotalSeconds) / (1024d * 1024 * 1024),
                        CurrentUnit = "GB/s",
                    });
                }
                double gbPerSec = bytesMoved / sw.Elapsed.TotalSeconds / (1024d * 1024 * 1024);

                // Random access: read 64 B at random offsets from a 64 MB array.
                const int randBufSize = 64 * 1024 * 1024;
                var rand = new byte[randBufSize];
                new Random(29).NextBytes(rand);
                var rng = new Random(31);
                long reads = 0;
                long accum = 0;
                var sw2 = Stopwatch.StartNew();
                var end2 = TimeSpan.FromSeconds(RamRandomSeconds);
                while (sw2.Elapsed < end2)
                {
                    if (ct.IsCancellationRequested)
                        break;
                    int off = rng.Next(0, randBufSize - 64);
                    for (int i = 0; i < 64; i++)
                        accum += rand[off + i];
                    reads++;
                }
                double nsPerRead = sw2.Elapsed.TotalMilliseconds * 1_000_000d / Math.Max(1, reads);

                GC.KeepAlive(accum);
                GC.KeepAlive(dst);
                return (gbPerSec, nsPerRead);
            }, ct);

            double score = Scoring.Normalize(ramResult.gbPerSec, Scoring.RefRamGbPerSec);
            return new BenchmarkSubScore
            {
                Key = "ram",
                Label = "RAM",
                Score = score,
                RawValue = Math.Round(ramResult.gbPerSec, 2),
                RawUnit = "GB/s",
                Detail = $"random access ~{Math.Round(ramResult.nsPerRead, 1)} ns/read",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new BenchmarkSubScore
            {
                Key = "ram",
                Label = "RAM",
                Score = 0,
                Detail = $"failed: {ex.Message}",
            };
        }
    }

    public async Task<BenchmarkSubScore> RunStorageAsync(
        IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct)
    {
        string? path = null;
        try
        {
            var storageResult = await Task.Run(() =>
            {
                path = Path.Combine(Path.GetTempPath(), $"nexus-bench-{Guid.NewGuid():N}.bin");
                const int chunk = 1 * 1024 * 1024;
                const long maxBytes = 4L * 1024 * 1024 * 1024;
                var block = new byte[chunk];
                new Random(53).NextBytes(block);

                // Sequential write
                long written = 0;
                var sw = Stopwatch.StartNew();
                using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, chunk, FileOptions.WriteThrough))
                {
                    var end = TimeSpan.FromSeconds(StorageSeqSeconds);
                    while (sw.Elapsed < end && written < maxBytes)
                    {
                        ct.ThrowIfCancellationRequested();
                        fs.Write(block, 0, chunk);
                        written += chunk;
                        progress.Report(new BenchmarkPhaseProgress
                        {
                            Phase = "storage",
                            Detail = "seq write",
                            Percent = Math.Min(1, sw.Elapsed.TotalSeconds / (StorageSeqSeconds * 2 + StorageRandomSeconds)),
                            CurrentRaw = written / sw.Elapsed.TotalSeconds / (1024d * 1024),
                            CurrentUnit = "MB/s",
                        });
                    }
                    fs.Flush();
                }
                double writeMbs = written / sw.Elapsed.TotalSeconds / (1024d * 1024);

                // Sequential read
                long read = 0;
                var sw2 = Stopwatch.StartNew();
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, chunk, FileOptions.SequentialScan))
                {
                    var end = TimeSpan.FromSeconds(StorageSeqSeconds);
                    while (sw2.Elapsed < end)
                    {
                        if (ct.IsCancellationRequested)
                            break;
                        if (fs.Position >= fs.Length)
                            fs.Seek(0, SeekOrigin.Begin);
                        int got = fs.Read(block, 0, chunk);
                        if (got <= 0)
                            break;
                        read += got;
                        progress.Report(new BenchmarkPhaseProgress
                        {
                            Phase = "storage",
                            Detail = "seq read",
                            Percent = Math.Min(1, (StorageSeqSeconds + sw2.Elapsed.TotalSeconds) / (StorageSeqSeconds * 2 + StorageRandomSeconds)),
                            CurrentRaw = read / sw2.Elapsed.TotalSeconds / (1024d * 1024),
                            CurrentUnit = "MB/s",
                        });
                    }
                }
                double readMbs = read / sw2.Elapsed.TotalSeconds / (1024d * 1024);

                // 4K random read
                long iops = 0;
                var four = new byte[4096];
                var rng = new Random(71);
                var sw3 = Stopwatch.StartNew();
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.RandomAccess))
                {
                    var end = TimeSpan.FromSeconds(StorageRandomSeconds);
                    long len = fs.Length;
                    while (sw3.Elapsed < end && len >= 4096)
                    {
                        if (ct.IsCancellationRequested)
                            break;
                        long off = ((long)rng.Next(0, (int)(len / 4096))) * 4096;
                        fs.Seek(off, SeekOrigin.Begin);
                        fs.ReadExactly(four, 0, 4096);
                        iops++;
                    }
                }
                double iopsPerSec = iops / Math.Max(0.001, sw3.Elapsed.TotalSeconds);
                return (writeMbs, readMbs, iopsPerSec);
            }, ct);

            // Composite raw: geometric mean of read/write MB/s, weighted against random IOPS
            double throughput = Math.Sqrt(storageResult.writeMbs * storageResult.readMbs);
            double iopsWeight = Math.Max(1, storageResult.iopsPerSec / 10); // 10 IOPS ~= 1 MB/s equivalent
            double composite = (throughput + iopsWeight) / 2;
            double score = Scoring.Normalize(composite, Scoring.RefStorageComposite);

            return new BenchmarkSubScore
            {
                Key = "storage",
                Label = "Storage",
                Score = score,
                RawValue = Math.Round(throughput, 1),
                RawUnit = "MB/s",
                Detail = $"read {Math.Round(storageResult.readMbs)} / write {Math.Round(storageResult.writeMbs)} MB/s | {Math.Round(storageResult.iopsPerSec)} IOPS",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new BenchmarkSubScore
            {
                Key = "storage",
                Label = "Storage",
                Score = 0,
                Detail = $"failed: {ex.Message}",
            };
        }
        finally
        {
            if (path is not null)
            {
                try
                { File.Delete(path); }
                catch { }
            }
        }
    }

    public async Task<BenchmarkSubScore> RunGpuAsync(
        IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct)
    {
        try
        {
            var gpuResult = await Task.Run(() =>
            {
                int threads = Math.Max(1, Environment.ProcessorCount);
                double[] flops = new double[threads];
                var end = TimeSpan.FromSeconds(GpuSeconds);
                var barrier = new Barrier(threads);

                var tasks = new Task[threads];
                for (int i = 0; i < threads; i++)
                {
                    int idx = i;
                    tasks[i] = Task.Run(() =>
                    {
                        flops[idx] = SimdComputeLoop(end, ct);
                    }, ct);
                }

                // Progress poller while compute runs. Uses the elapsed wall time to
                // estimate percent - can't peek into the workers without adding
                // synchronisation overhead to the hot loop.
                var sw = Stopwatch.StartNew();
                while (!Task.WaitAll(tasks, 200))
                {
                    if (ct.IsCancellationRequested)
                        break;
                    progress.Report(new BenchmarkPhaseProgress
                    {
                        Phase = "gpu",
                        Detail = "SIMD compute",
                        Percent = Math.Min(1, sw.Elapsed.TotalSeconds / GpuSeconds),
                        CurrentRaw = 0,
                        CurrentUnit = "GFLOPS",
                    });
                }

                double total = 0;
                for (int i = 0; i < threads; i++)
                    total += flops[i];
                return total;
            }, ct);

            double gflops = gpuResult / 1_000_000_000d;
            double score = Scoring.Normalize(gflops, Scoring.RefGpuGflops);

            return new BenchmarkSubScore
            {
                Key = "gpu",
                Label = "GPU (compute)",
                Score = score,
                RawValue = Math.Round(gflops, 1),
                RawUnit = "GFLOPS",
                Detail = "SIMD compute proxy (real GPU test planned)",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new BenchmarkSubScore
            {
                Key = "gpu",
                Label = "GPU",
                Score = 0,
                Detail = $"failed: {ex.Message}",
            };
        }
    }

    private static double SimdComputeLoop(TimeSpan duration, CancellationToken ct)
    {
        int lanes = Vector<float>.Count;
        var a = new Vector<float>(1.0001f);
        var b = new Vector<float>(0.9999f);
        var c = Vector<float>.One;

        // Each iteration: c = c * a + b  -> 2 * lanes float ops per iter.
        long iters = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration)
        {
            if (ct.IsCancellationRequested)
                break;
            // Unroll 16x to amortise loop overhead.
            for (int i = 0; i < 16; i++)
            {
                c = (c * a) + b;
                c = (c * a) + b;
                c = (c * a) + b;
                c = (c * a) + b;
            }
            iters += 64;
        }
        GC.KeepAlive(c);
        double elapsed = sw.Elapsed.TotalSeconds;
        double opsPerIter = 2d * lanes;
        return iters * opsPerIter / Math.Max(0.001, elapsed);
    }
}
