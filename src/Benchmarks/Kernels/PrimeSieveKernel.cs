using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Nexus.Service.Benchmarks.Kernels;

/// <summary>
/// Odd-only segmented sieve of Eratosthenes, counting primes below a limit.
/// The portable stand-in for the bundled primesieve CLI on platforms that have
/// no bench bundle; reports the same primes/s rate so the axis keeps its unit.
/// Rates from this kernel are far below primesieve's wheel-factorised ones and
/// are only comparable within the portable scoring version.
/// </summary>
internal static class PrimeSieveKernel
{
    /// <summary>Odd candidates per segment; the flag buffer stays inside a typical L2.</summary>
    internal const int SegmentOdds = 256 * 1024;

    /// <summary>
    /// Counts primes in [0, limit) across <paramref name="threads"/> workers.
    /// <paramref name="onProgress"/> receives the completed fraction in [0, 1].
    /// </summary>
    internal static long CountPrimes(long limit, int threads, Action<double>? onProgress, CancellationToken ct)
    {
        if (limit < 3)
        {
            return limit > 2 ? 1 : 0;
        }

        var basePrimes = BasePrimesUpToSqrt(limit);

        // Odd candidates start at 3; segment i covers [3 + 2*i*SegmentOdds, ...).
        long totalOdds = (limit - 1) / 2;
        long segmentCount = (totalOdds + SegmentOdds - 1) / SegmentOdds;
        long completed = 0;
        long oddPrimes = 0;
        long nextSegment = -1;
        Exception? failure = null;

        // Nothing escapes: on a bare thread an unhandled exception takes the
        // whole service down, so a fault is recorded and rethrown by the caller.
        void Worker()
        {
            try
            {
                var flags = new byte[SegmentOdds];
                long local = 0;
                while (true)
                {
                    long segment = Interlocked.Increment(ref nextSegment);
                    if (segment >= segmentCount || ct.IsCancellationRequested)
                    {
                        break;
                    }

                    local += CountSegment(flags, segment, limit, basePrimes);

                    long done = Interlocked.Increment(ref completed);
                    onProgress?.Invoke((double)done / segmentCount);
                }
                Interlocked.Add(ref oddPrimes, local);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
            }
        }

        if (threads <= 1)
        {
            Worker();
        }
        else
        {
            var workers = new Thread[threads];
            for (int i = 0; i < threads; i++)
            {
                workers[i] = new Thread(Worker) { IsBackground = true, Name = $"nexus-bench-cpu-{i}" };
                workers[i].Start();
            }
            foreach (var w in workers)
            {
                w.Join();
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        ct.ThrowIfCancellationRequested();

        // The sieve only walks odd candidates from 3 up, so 2 is counted here.
        return oddPrimes + 1;
    }

    /// <summary>Marks composites in one segment and returns its prime count.</summary>
    private static long CountSegment(byte[] flags, long segment, long limit, IReadOnlyList<int> basePrimes)
    {
        long firstOdd = 3 + 2L * segment * SegmentOdds;
        long maxOdd = (limit - 1) | 1;
        if (maxOdd >= limit)
        {
            maxOdd -= 2;
        }
        long lastOdd = Math.Min(firstOdd + 2L * (SegmentOdds - 1), maxOdd);
        if (lastOdd < firstOdd)
        {
            return 0;
        }

        int slots = (int)((lastOdd - firstOdd) / 2) + 1;
        Array.Clear(flags, 0, slots);

        foreach (int p in basePrimes)
        {
            long square = (long)p * p;
            if (square > lastOdd)
            {
                break;
            }

            // First odd multiple of p at or above the segment start; below p*p
            // every multiple already carries a smaller factor, so start there.
            long start;
            if (square >= firstOdd)
            {
                start = square;
            }
            else
            {
                long rem = firstOdd % p;
                start = rem == 0 ? firstOdd : firstOdd + (p - rem);
                if ((start & 1) == 0)
                {
                    start += p;
                }
            }

            for (long m = start; m <= lastOdd; m += 2L * p)
            {
                flags[(int)((m - firstOdd) / 2)] = 1;
            }
        }

        long count = 0;
        for (int i = 0; i < slots; i++)
        {
            if (flags[i] == 0)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>Odd primes up to sqrt(limit), the marking set every segment reuses.</summary>
    private static int[] BasePrimesUpToSqrt(long limit)
    {
        int root = (int)Math.Sqrt(limit) + 1;
        var composite = new bool[root + 1];
        var primes = new List<int>();
        for (int i = 3; i <= root; i += 2)
        {
            if (composite[i])
            {
                continue;
            }
            primes.Add(i);
            for (long j = (long)i * i; j <= root; j += 2L * i)
            {
                composite[(int)j] = true;
            }
        }
        return primes.ToArray();
    }

    /// <summary>Primes/s over [0, limit), measured across <paramref name="threads"/> workers.</summary>
    internal static (double primesPerSec, long primes, double seconds) Measure(
        long limit, int threads, Action<double>? onProgress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long primes = CountPrimes(limit, threads, onProgress, ct);
        sw.Stop();
        double seconds = sw.Elapsed.TotalSeconds;
        return (seconds > 0 ? primes / seconds : 0, primes, seconds);
    }
}
