using System;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Benchmarks;
using Nexus.Service.Benchmarks.Kernels;
using Nexus.Service.Benchmarks.Providers;
using Nexus.Service.DependencyInjection;
using Nexus.Service.Models.Benchmarks;

namespace Nexus.Service.Tests;

/// <summary>
/// The in-process kernels and provider that run the benchmark on macOS and
/// Linux, where the bundled bench CLIs have no build. Workloads here are sized
/// for a test run, not for scoring - the provider owns the production sizes.
/// </summary>
public class PortableBenchmarkTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Theory]
    [InlineData(10L, 4L)]
    [InlineData(100L, 25L)]
    [InlineData(1_000L, 168L)]
    [InlineData(100_000L, 9_592L)]
    [InlineData(1_000_000L, 78_498L)]
    public void PrimeSieve_SingleThreaded_MatchesKnownPrimeCounts(long limit, long expected)
    {
        Assert.Equal(expected, PrimeSieveKernel.CountPrimes(limit, 1, null, None));
    }

    [Theory]
    [InlineData(1_000L, 168L)]
    [InlineData(1_000_000L, 78_498L)]
    public void PrimeSieve_MultiThreaded_MatchesSingleThreadedCount(long limit, long expected)
    {
        // Segments are handed out by an interlocked counter, so a partitioning
        // or per-segment-state bug shows up as a count that drifts from pi(x).
        Assert.Equal(expected, PrimeSieveKernel.CountPrimes(limit, 4, null, None));
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 0L)]
    [InlineData(2L, 0L)]
    [InlineData(3L, 1L)]
    [InlineData(4L, 2L)]
    public void PrimeSieve_BelowFirstSegment_CountsEdgeCases(long limit, long expected)
    {
        // The sieve walks odd candidates from 3 and adds 2 afterwards, so the
        // limits either side of 2 and 3 are where an off-by-one surfaces.
        Assert.Equal(expected, PrimeSieveKernel.CountPrimes(limit, 1, null, None));
    }

    [Fact]
    public void PrimeSieve_SpansMultipleSegments_StillMatchesKnownCount()
    {
        long limit = 4L * PrimeSieveKernel.SegmentOdds * 2 + 1000;
        long expected = PrimeSieveKernel.CountPrimes(limit, 1, null, None);
        Assert.Equal(expected, PrimeSieveKernel.CountPrimes(limit, 3, null, None));
        Assert.True(expected > 0);
    }

    [Fact]
    public void PrimeSieve_Measure_ReportsPositiveRate()
    {
        var (rate, primes, seconds) = PrimeSieveKernel.Measure(1_000_000, 1, null, None);
        Assert.Equal(78_498, primes);
        Assert.True(rate > 0);
        Assert.True(seconds > 0);
    }

    [Fact]
    public void Triad_MultiThreaded_ProducesPositiveRatePerPass()
    {
        var passes = TriadKernel.Measure(3, 4, null, None, elementCount: 1 << 16);
        Assert.Equal(3, passes.Length);
        Assert.All(passes, p => Assert.True(p > 0, $"pass rate was {p}"));
    }

    [Fact]
    public void Triad_ReportsProgressOncePerPass()
    {
        int reports = 0;
        TriadKernel.Measure(4, 2, _ => Interlocked.Increment(ref reports), None, elementCount: 1 << 16);
        Assert.Equal(4, reports);
    }

    [Fact]
    public void Triad_ElementCountNotDivisibleByWorkers_StillCoversEveryElement()
    {
        // The last worker takes the remainder; a rate of zero would mean a pass
        // completed with no work, and a crash would mean the tail ran off.
        var passes = TriadKernel.Measure(1, 3, null, None, elementCount: 1000);
        Assert.True(passes[0] > 0);
    }

    [Fact]
    public void Storage_Measure_ReportsSequentialAndRandomResults()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"nexus-bench-test-{Guid.NewGuid():N}.bin");
        try
        {
            var result = StorageKernel.Measure(
                path, null, None,
                sequentialTotalBytes: 4L * 1024 * 1024,
                randomOpCount: 16,
                sequentialTrials: 2);

            Assert.Equal(2, result.SequentialTrialsMbPerSec.Length);
            Assert.All(result.SequentialTrialsMbPerSec, v => Assert.True(v > 0, $"trial rate was {v}"));
            Assert.True(result.RandomIops > 0);
            Assert.True(result.AverageLatencyMs > 0);
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Storage_Measure_RemovesNothingButLeavesFileForCaller()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"nexus-bench-test-{Guid.NewGuid():N}.bin");
        try
        {
            StorageKernel.Measure(
                path, null, None,
                sequentialTotalBytes: 1024 * 1024, randomOpCount: 4, sequentialTrials: 1);
            Assert.True(System.IO.File.Exists(path));
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Triad_CancelledMidRun_ThrowsWithoutStrandingWorkers()
    {
        // Leaving the pass loop early would park the workers at a barrier the
        // coordinator never reaches, and the using-scope would then dispose it
        // underneath them. Cancellation has to unwind through the joins.
        using var cts = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() =>
            TriadKernel.Measure(
                6, 4,
                _ => cts.Cancel(),
                cts.Token,
                elementCount: 1 << 16));
    }

    [Fact]
    public void PrimeSieve_CancelledMidRun_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            PrimeSieveKernel.CountPrimes(10_000_000, 4, null, cts.Token));
    }

    [Fact]
    public async Task Provider_CpuCancelledMidRun_DoesNotLeakTheCancellation()
    {
        // Cancellation is the one exception IBenchmarkProvider propagates -
        // BenchmarkRunner catches it to mark the run cancelled.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ManagedBenchmarkProvider().RunCpuAsync(
                new Progress<BenchmarkPhaseProgress>(), cts.Token));
    }

    [Fact]
    public void Scoring_RelativeSpread_SeparatesAgreeingTrialsFromNoise()
    {
        // The GPU axis discards a rate whose trials disagree past 0.5, which is
        // what a driver with an unreliable draw clock produces. Measurements
        // from hardware that is really being timed sit far below it.
        Assert.True(Scoring.RelativeSpread(new List<double> { 6359, 6431, 6490 }) < 0.5);
        Assert.True(Scoring.RelativeSpread(new List<double> { 748, 1231, 7103 }) > 0.5);
    }

    [Fact]
    public void Provider_ScoringVersion_IsThePortableMethodology()
    {
        Assert.Equal("portable-v1.0-2026.08", new ManagedBenchmarkProvider().ScoringVersion);
    }

    [Fact]
    public void Provider_ScoringVersion_NeverCollidesWithTheBundledToolVersion()
    {
        // The leaderboard partitions on this string. A collision would rank an
        // in-process measurement against a bundled-tool one.
        Assert.NotEqual(
            new ExternalToolBenchmarkProvider().ScoringVersion,
            new ManagedBenchmarkProvider().ScoringVersion);
    }

    [Fact]
    public void Provider_Baselines_MatchThePortableScoringConstants()
    {
        var baselines = new ManagedBenchmarkProvider().Baselines;
        Assert.Equal(Scoring.PortableBaselineCpuPrimesPerSec, baselines.Cpu);
        Assert.Equal(Scoring.PortableBaselineGpuGflops, baselines.Gpu);
        Assert.Equal(Scoring.PortableBaselineRamGbPerSec, baselines.Ram);
        Assert.Equal(Scoring.PortableBaselineStorageMbPerSec, baselines.Storage);
    }

    [Fact]
    public void ExternalToolProvider_Baselines_MatchTheBundledScoringConstants()
    {
        var baselines = new ExternalToolBenchmarkProvider().Baselines;
        Assert.Equal(Scoring.BaselineCpuPrimesPerSec, baselines.Cpu);
        Assert.Equal(Scoring.BaselineGpuGflops, baselines.Gpu);
        Assert.Equal(Scoring.BaselineRamGbPerSec, baselines.Ram);
        Assert.Equal(Scoring.BaselineStorageMbPerSec, baselines.Storage);
    }

    [Fact]
    public async Task Provider_GpuWithoutContext_ScoresZeroWithoutThrowing()
    {
        // IBenchmarkProvider forbids throwing; a headless Linux box with no EGL
        // device has no context, and that axis must degrade, not fail the run.
        var sub = await new ManagedBenchmarkProvider(null).RunGpuAsync(
            new Progress<BenchmarkPhaseProgress>(), None);

        Assert.Equal("gpu", sub.Key);
        Assert.Equal(0, sub.Score);
        Assert.Contains("no GPU context", sub.Detail);
    }

    [Fact]
    public void Di_SelectsTheProviderThatCanActuallyRunOnThisPlatform()
    {
        // The bench bundle ships for Windows only, so anywhere else the
        // external-tool provider reports every axis as "tool not bundled".
        using var provider = new ServiceCollection()
            .AddNexusBenchmarks()
            .BuildServiceProvider();

        var selected = provider.GetRequiredService<IBenchmarkProvider>();
        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<ExternalToolBenchmarkProvider>(selected);
        }
        else
        {
            Assert.IsType<ManagedBenchmarkProvider>(selected);
        }
    }

    [Fact]
    public void Di_SelectedProviderDeclaresAScoringVersionTheApiWillAccept()
    {
        using var provider = new ServiceCollection()
            .AddNexusBenchmarks()
            .BuildServiceProvider();

        var selected = provider.GetRequiredService<IBenchmarkProvider>();
        Assert.False(string.IsNullOrWhiteSpace(selected.ScoringVersion));
        Assert.True(selected.Baselines.Cpu > 0);
        Assert.True(selected.Baselines.Gpu > 0);
        Assert.True(selected.Baselines.Ram > 0);
        Assert.True(selected.Baselines.Storage > 0);
    }
}
