using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Nexus.Service.Benchmarks.Kernels;

/// <summary>
/// STREAM Triad (<c>a[i] = b[i] + q * c[i]</c>) in managed code, the portable
/// stand-in for the bundled stream.exe. Follows STREAM's accounting: 24 bytes
/// of traffic per element (two reads + one write) and several passes the caller
/// reduces, so the GB/s figure means the same thing as the bundled tool's.
/// </summary>
internal static class TriadKernel
{
    /// <summary>
    /// Doubles per array, sized by STREAM's rule that each array must exceed
    /// the last-level cache several times over or the loop measures cache
    /// rather than memory. Fixed, not machine-derived, so the rate stays
    /// comparable across machines in this scoring version.
    /// </summary>
    internal const int ElementCount = 12 * 1024 * 1024;

    internal const int BytesPerElement = 24;

    /// <summary>
    /// Timed window a pass aims for. A single sweep of the arrays takes about
    /// as long as barrier wake-up jitter on a machine with fast memory, which
    /// leaves the rate dominated by scheduling noise, so a pass repeats the
    /// sweep until it fills this window. The count is calibrated per machine;
    /// the reported figure stays a rate either way.
    /// </summary>
    private static readonly TimeSpan PassTarget = TimeSpan.FromMilliseconds(150);

    private const int MinSweepsPerPass = 4;
    private const int MaxSweepsPerPass = 4096;

    /// <summary>Scalar multiplier; STREAM's own Triad constant.</summary>
    private const double Q = 3.0;

    /// <summary>
    /// Runs <paramref name="passes"/> Triad passes and returns each pass's GB/s.
    /// Workers are created once and reused across every pass: creating them per
    /// pass costs more than the pass itself and reads as negative scaling.
    /// </summary>
    internal static double[] Measure(
        int passes, int threads, Action<double>? onProgress, CancellationToken ct,
        int elementCount = ElementCount)
    {
        var a = new double[elementCount];
        var b = new double[elementCount];
        var c = new double[elementCount];

        int workerCount = Math.Max(1, threads);
        var results = new double[passes];
        double bytesPerSweep = (double)elementCount * BytesPerElement;

        // Cycle 0 is a calibration pass: the coordinator times it, sizes the
        // remaining passes from it, and publishes the count before releasing
        // the workers again. The barrier orders that write against their read.
        int sweeps = MinSweepsPerPass;

        // The coordinator is a participant, so it releases the workers and
        // rejoins them at known points and can time the span between.
        using var barrier = new Barrier(workerCount + 1);
        var workers = new Thread[workerCount];
        Exception? failure = null;

        for (int t = 0; t < workerCount; t++)
        {
            int index = t;
            workers[t] = new Thread(() =>
            {
                int perWorker = elementCount / workerCount;
                int from = index * perWorker;
                int to = index == workerCount - 1 ? elementCount : from + perWorker;
                bool aborted = false;

                // A worker must reach every barrier cycle even after it fails,
                // or the coordinator parks forever waiting for a participant
                // that is gone. It records the fault and keeps signalling; an
                // escaping exception on a bare thread would take the whole
                // service down, so nothing is rethrown here.
                for (int pass = 0; pass <= passes + 1; pass++)
                {
                    if (pass == 0)
                    {
                        try
                        {
                            // First touch by the worker that will keep reading
                            // these pages, so page faults land outside the timed
                            // region and NUMA hosts place them near this thread.
                            for (int i = from; i < to; i++)
                            {
                                a[i] = 0;
                                b[i] = 1.0;
                                c[i] = 2.0;
                            }
                        }
                        catch (Exception ex)
                        {
                            Interlocked.CompareExchange(ref failure, ex, null);
                            aborted = true;
                        }
                        continue;
                    }

                    barrier.SignalAndWait();
                    if (!aborted)
                    {
                        try
                        {
                            int count = Volatile.Read(ref sweeps);
                            for (int sweep = 0; sweep < count; sweep++)
                            {
                                Sweep(a, b, c, from, to);
                            }
                        }
                        catch (Exception ex)
                        {
                            Interlocked.CompareExchange(ref failure, ex, null);
                            aborted = true;
                        }
                    }
                    barrier.SignalAndWait();
                }
            })
            { IsBackground = true, Name = $"nexus-bench-ram-{index}" };
            workers[t].Start();
        }

        for (int pass = 0; pass <= passes; pass++)
        {
            barrier.SignalAndWait();
            var sw = Stopwatch.StartNew();
            barrier.SignalAndWait();
            sw.Stop();

            double seconds = sw.Elapsed.TotalSeconds;
            int count = sweeps;
            if (pass == 0)
            {
                double perSweep = seconds / count;
                long target = perSweep > 0
                    ? (long)Math.Ceiling(PassTarget.TotalSeconds / perSweep)
                    : MinSweepsPerPass;
                Volatile.Write(ref sweeps, (int)Math.Clamp(target, MinSweepsPerPass, MaxSweepsPerPass));
                continue;
            }

            results[pass - 1] = seconds > 0 ? bytesPerSweep * count / seconds / 1e9 : 0;
            onProgress?.Invoke((double)pass / passes);

            // Leaving the loop early would strand the workers at a barrier the
            // coordinator never reaches, and disposing it under them is
            // undefined. Shrink the remaining passes instead and throw once
            // everyone has been joined.
            if (ct.IsCancellationRequested)
            {
                Volatile.Write(ref sweeps, 1);
            }
        }

        foreach (var w in workers)
        {
            w.Join();
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        ct.ThrowIfCancellationRequested();

        GC.KeepAlive(a);
        return results;
    }

    private static void Sweep(double[] a, double[] b, double[] c, int from, int to)
    {
        int width = Vector<double>.Count;
        var q = new Vector<double>(Q);
        int i = from;
        int vectorEnd = to - (to - from) % width;
        for (; i < vectorEnd; i += width)
        {
            var vb = new Vector<double>(b, i);
            var vc = new Vector<double>(c, i);
            (vb + q * vc).CopyTo(a, i);
        }
        for (; i < to; i++)
        {
            a[i] = b[i] + Q * c[i];
        }
    }
}
