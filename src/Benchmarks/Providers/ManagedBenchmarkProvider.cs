using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Benchmarks.Kernels;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Models.Benchmarks;

namespace Nexus.Service.Benchmarks.Providers;

/// <summary>
/// In-process benchmark provider for platforms with no bench bundle. The
/// bundled CLIs the Windows path drives (primesieve, clpeak, STREAM, DiskSpd)
/// have no macOS or Linux binaries in the tree, so on those platforms
/// <see cref="ExternalToolBenchmarkProvider"/> reports every axis as "tool not
/// bundled" and the composite floors at 1. These kernels measure the same four
/// subsystems with the same units, under their own scoring version.
/// </summary>
public sealed class ManagedBenchmarkProvider : IBenchmarkProvider
{
    /// <summary>Sieve limit for the single-threaded pass; a few seconds on a slow core.</summary>
    private const long CpuSingleLimit = 500_000_000L;

    /// <summary>Sieve limit for the all-core pass. Fixed rather than core-scaled: primes thin out as the limit grows, so a machine-dependent limit would not compare.</summary>
    private const long CpuAllCoreLimit = 2_000_000_000L;

    /// <summary>
    /// Sieve passes per mode, of which the best is kept: other load on the
    /// machine only ever slows a pass down, so the fastest is the closest
    /// estimate of what the hardware can do.
    /// </summary>
    private const int CpuTrials = 3;

    /// <summary>
    /// Budget for the whole CPU axis. Trials past it are dropped, keeping at
    /// least one, so slow hardware does not hold every core for minutes.
    /// </summary>
    private static readonly TimeSpan CpuTrialBudget = TimeSpan.FromSeconds(45);

    private const int RamPasses = 7;

    /// <summary>
    /// Trial-to-trial spread above which the GPU rate is discarded. Real
    /// hardware agrees to within a few percent; this sits far above that and
    /// well below the disagreement an untrustworthy draw clock produces.
    /// </summary>
    private const double MaxGpuTrialSpread = 0.5;

    /// <summary>Matches the cadence the bundled-tool path reports at.</summary>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(500);

    private readonly GpuContext? _gpu;
    private readonly Dictionary<string, string> _collectedTools = new();
    private long _lastReportTicks;

    public ManagedBenchmarkProvider(GpuContext? gpu = null)
    {
        _gpu = gpu;
    }

    public IReadOnlyDictionary<string, string> CollectedTools => _collectedTools;

    public string ScoringVersion => Scoring.PortableScoringVersion;

    public BenchmarkBaselines Baselines => new()
    {
        Cpu = Scoring.PortableBaselineCpuPrimesPerSec,
        Gpu = Scoring.PortableBaselineGpuGflops,
        Ram = Scoring.PortableBaselineRamGbPerSec,
        Storage = Scoring.PortableBaselineStorageMbPerSec,
    };

    private static BenchmarkSubScore Failure(string key, string label, string detail)
        => new() { Key = key, Label = label, Score = 0, Detail = detail };

    public Task<BenchmarkSubScore> RunCpuAsync(
        IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct)
        => Task.Run(() =>
        {
            try
            {
                _collectedTools["cpu"] = "nexus segmented sieve";

                var clock = Stopwatch.StartNew();
                var singleTrials = RunSieveTrials(
                    CpuSingleLimit, 1, "single-core", 0, 0.4, clock, progress, ct);
                var allCoreTrials = RunSieveTrials(
                    CpuAllCoreLimit, Environment.ProcessorCount, "all-core", 0.4, 1.0, clock, progress, ct);

                double singleBest = singleTrials.Count > 0 ? singleTrials.Max() : 0;
                double allCoreBest = allCoreTrials.Count > 0 ? allCoreTrials.Max() : 0;
                if (allCoreBest <= 0 && singleBest <= 0)
                {
                    return Failure("cpu", "CPU", "sieve produced no rate");
                }

                bool scoredAllCore = allCoreBest > 0;
                double raw = scoredAllCore ? allCoreBest : singleBest;
                double singleG = Math.Round(singleBest / 1e9, 4);
                double rawG = Math.Round(raw / 1e9, 4);
                var scoredTrials = (scoredAllCore ? allCoreTrials : singleTrials)
                    .Select(v => Math.Round(v / 1e9, 4)).ToArray();

                return new BenchmarkSubScore
                {
                    Key = "cpu",
                    Label = "CPU",
                    Score = Scoring.Score(raw, Scoring.PortableBaselineCpuPrimesPerSec),
                    RawValue = rawG,
                    RawUnit = "Gprimes/s",
                    Detail = scoredAllCore
                        ? $"single {singleG} Gprimes/s | all-core {rawG} Gprimes/s (best of {scoredTrials.Length})"
                        : $"single {rawG} Gprimes/s (all-core produced no rate)",
                    Trials = scoredTrials,
                    Spread = Scoring.RelativeSpread(scoredTrials),
                    SingleCoreRawValue = singleG,
                    SingleCoreRawUnit = "Gprimes/s",
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Failure("cpu", "CPU", $"error: {ex.Message}");
            }
        }, ct);

    public Task<BenchmarkSubScore> RunRamAsync(
        IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct)
        => Task.Run(() =>
        {
            try
            {
                _collectedTools["ram"] = "nexus STREAM Triad";

                progress.Report(new BenchmarkPhaseProgress { Phase = "ram", Detail = "STREAM Triad", Percent = 0 });
                var passes = TriadKernel.Measure(
                    RamPasses, Environment.ProcessorCount,
                    p => Report(progress, "ram", "STREAM Triad", 0, 1, p), ct);

                // Best, not median: STREAM reports its own best rate, which is
                // what the bundled stream.exe's parsed "Triad:" line carries, and
                // it is the pass least polluted by other load on the machine.
                double best = passes.Length > 0 ? passes.Max() : 0;
                if (best <= 0)
                {
                    return Failure("ram", "RAM", "triad produced no rate");
                }

                return new BenchmarkSubScore
                {
                    Key = "ram",
                    Label = "RAM",
                    Score = Scoring.Score(best, Scoring.PortableBaselineRamGbPerSec),
                    RawValue = Math.Round(best, 2),
                    RawUnit = "GB/s",
                    Detail = $"STREAM Triad {Math.Round(best, 2)} GB/s (best of {passes.Length})",
                    Trials = passes,
                    Spread = Scoring.RelativeSpread(passes),
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Failure("ram", "RAM", $"error: {ex.Message}");
            }
        }, ct);

    public Task<BenchmarkSubScore> RunStorageAsync(
        IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var path = Path.Combine(ScratchDirectory(), $"nexus-bench-{Guid.NewGuid():N}.bin");
            try
            {
                _collectedTools["storage"] = "nexus write probe";

                progress.Report(new BenchmarkPhaseProgress { Phase = "storage", Detail = "sequential write", Percent = 0 });
                var measurement = StorageKernel.Measure(
                    path,
                    p => Report(progress, "storage", p < 0.7 ? "sequential write" : "4K random write", 0, 1, p),
                    ct);

                var trials = measurement.SequentialTrialsMbPerSec.Where(v => v > 0).ToArray();
                double seq = Scoring.Median(trials);
                if (seq <= 0 && measurement.RandomIops <= 0)
                {
                    return Failure("storage", "Storage", "write probe produced no rate");
                }

                string detail = $"seq write {Math.Round(seq)} MB/s";
                if (measurement.RandomIops > 0)
                {
                    detail += $" | 4K {Math.Round(measurement.RandomIops)} IOPS";
                }
                if (measurement.AverageLatencyMs > 0)
                {
                    detail += $" | lat {Math.Round(measurement.AverageLatencyMs, 3)} ms";
                }

                return new BenchmarkSubScore
                {
                    Key = "storage",
                    Label = "Storage",
                    Score = Scoring.Score(seq, Scoring.PortableBaselineStorageMbPerSec),
                    RawValue = Math.Round(seq, 1),
                    RawUnit = "MB/s",
                    Detail = detail,
                    Trials = trials.Length > 0 ? trials : null,
                    Spread = Scoring.RelativeSpread(trials),
                    RandomIops = Math.Round(measurement.RandomIops),
                    LatencyMs = Math.Round(measurement.AverageLatencyMs, 3),
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Failure("storage", "Storage", $"error: {ex.Message}");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }, ct);

    public Task<BenchmarkSubScore> RunGpuAsync(
        IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct)
        => Task.Run(() =>
        {
            if (_gpu is null)
            {
                return Failure("gpu", "GPU (compute)", "no GPU context available");
            }

            try
            {
                _collectedTools["gpu"] = "nexus shader FMA (OpenGL)";
                progress.Report(new BenchmarkPhaseProgress { Phase = "gpu", Detail = "shader FMA", Percent = 0 });
                var measurement = GpuFlopsKernel.Measure(
                    _gpu, p => Report(progress, "gpu", "shader FMA", 0, 1, p), ct);

                var trials = measurement.TrialGflops;
                if (trials.Length == 0)
                {
                    // Every trial rejected its own trip-count doubling, so the
                    // FLOP total cannot be derived from it. Same diagnosis as
                    // the calibration gate, reached one stage later when the
                    // driver is erratic rather than consistently wrong.
                    return Failure("gpu", "GPU (compute)",
                        "no rate: doubling the shader trip count cost no extra time "
                        + "(the driver optimised the loop away, or its glFinish does not wait)");
                }

                // Trials that disagree this badly mean the clock, not the GPU,
                // is what varied - a driver whose glFinish does not wait times
                // as noise, and noise occasionally lands on a plausible-looking
                // rate. Hardware that really is being measured agrees closely.
                double spread = Scoring.RelativeSpread(trials);
                if (trials.Length < 2 || spread > MaxGpuTrialSpread)
                {
                    return Failure("gpu", "GPU (compute)",
                        $"no reliable rate: {trials.Length} trial(s), spread {Math.Round(spread, 3)} "
                        + "(the driver's draw timing is not trustworthy on this host)");
                }

                double gflops = Scoring.Median(trials);

                return new BenchmarkSubScore
                {
                    Key = "gpu",
                    Label = "GPU (compute)",
                    Score = Scoring.Score(gflops, Scoring.PortableBaselineGpuGflops),
                    RawValue = Math.Round(gflops, 1),
                    RawUnit = "GFLOPS",
                    Detail = $"{Math.Round(gflops, 1)} GFLOPS sp (median of {trials.Length})",
                    Trials = trials,
                    Spread = Scoring.RelativeSpread(trials),
                    MeasuredDevice = measurement.Renderer,
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Failure("gpu", "GPU (compute)", $"error: {ex.Message}");
            }
        }, ct);

    /// <summary>
    /// Sieve trials for one mode, stopping early once <see cref="CpuTrialBudget"/>
    /// is spent. Always runs at least one, so the axis still reports on hardware
    /// slow enough that a single pass exhausts the budget.
    /// </summary>
    private List<double> RunSieveTrials(
        long limit, int threads, string detail, double start, double end,
        Stopwatch clock, IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct)
    {
        var rates = new List<double>(CpuTrials);
        for (int i = 0; i < CpuTrials; i++)
        {
            if (i > 0 && clock.Elapsed > CpuTrialBudget)
            {
                break;
            }
            double from = start + (end - start) * i / CpuTrials;
            double to = start + (end - start) * (i + 1) / CpuTrials;
            rates.Add(PrimeSieveKernel.Measure(
                limit, threads,
                p => Report(progress, "cpu", detail, from, to, p), ct).primesPerSec);
        }
        return rates;
    }

    /// <summary>
    /// Where the storage probe writes. Not <see cref="Path.GetTempPath"/>: on
    /// many Linux distributions /tmp is tmpfs, and the probe would measure RAM
    /// and report it as a drive. The Nexus data root is on real storage on
    /// every platform. Falls back to the temp path if it cannot be created.
    /// </summary>
    private static string ScratchDirectory()
    {
        try
        {
            var dir = Path.Combine(Persistence.NexusDataPaths.NexusRoot(), "bench");
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            return Path.GetTempPath();
        }
    }

    /// <summary>
    /// Maps a kernel's [0, 1] completion onto the phase's slice of the run,
    /// rate-limited to <see cref="ReportInterval"/>. Every frame is a websocket
    /// broadcast and the kernels call this per unit of work - the all-core sieve
    /// alone finishes thousands of segments - so an unthrottled forward would
    /// flood every subscriber. Racing callers may both pass the check; the cost
    /// is one extra frame, so the counter is not worth locking.
    /// </summary>
    private void Report(
        IProgress<BenchmarkPhaseProgress> progress, string phase, string detail,
        double start, double end, double fraction)
    {
        long now = Stopwatch.GetTimestamp();
        if (fraction < 1 &&
            Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastReportTicks), now) < ReportInterval)
        {
            return;
        }
        Interlocked.Exchange(ref _lastReportTicks, now);

        progress.Report(new BenchmarkPhaseProgress
        {
            Phase = phase,
            Detail = detail,
            Percent = start + (end - start) * Math.Clamp(fraction, 0, 1),
        });
    }
}
