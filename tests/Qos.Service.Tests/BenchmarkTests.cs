using System;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Benchmarks;
using Qos.Service.Benchmarks.Providers;
using Qos.Service.Models.Benchmarks;

namespace Qos.Service.Tests;

public class BenchmarkTests
{
    [Fact]
    public void Scoring_Normalize_ZeroReference_ReturnsZero()
    {
        Assert.Equal(0, Scoring.Normalize(100, 0));
    }

    [Fact]
    public void Scoring_Normalize_AtReference_ReturnsThousand()
    {
        Assert.Equal(1000, Scoring.Normalize(Scoring.RefCpuHashesPerSec, Scoring.RefCpuHashesPerSec));
    }

    [Fact]
    public void Scoring_WeightedGeoMean_AllEqual_ReturnsInput()
    {
        var c = Scoring.WeightedGeoMean(1000, 1000, 1000, 1000);
        Assert.InRange(c, 999, 1001);
    }

    [Fact]
    public void Scoring_WeightedGeoMean_WeakStorageHurtsComposite()
    {
        // Same cpu/gpu/ram. Storage drops 90%. Composite should drop noticeably.
        var balanced = Scoring.WeightedGeoMean(1000, 1000, 1000, 1000);
        var weakStorage = Scoring.WeightedGeoMean(1000, 1000, 1000, 100);
        Assert.True(weakStorage < balanced * 0.75);
    }

    // The four "ProducesPositiveScore" tests actually run the benchmarks
    // end-to-end (20s + 20s + 9s + 8s ≈ 57s, ~91% of the suite). They're
    // tagged Manual so the default `dotnet test` (and the build-pc + pre-push
    // gates) skip them. Run them explicitly with:
    //   dotnet test --filter Category=Manual
    [Fact(Timeout = 60_000), Trait("Category", "Manual")]
    public async Task Cpu_ProducesPositiveScore()
    {
        var provider = new DefaultBenchmarkProvider();
        var progress = new Progress<BenchmarkPhaseProgress>(_ => { });
        var result = await provider.RunCpuAsync(progress, CancellationToken.None);
        Assert.Equal("cpu", result.Key);
        Assert.True(result.Score > 0, $"expected positive CPU score, got {result.Score}");
        Assert.True(result.RawValue > 0);
    }

    [Fact(Timeout = 30_000), Trait("Category", "Manual")]
    public async Task Ram_ProducesPositiveScore()
    {
        var provider = new DefaultBenchmarkProvider();
        var progress = new Progress<BenchmarkPhaseProgress>(_ => { });
        var result = await provider.RunRamAsync(progress, CancellationToken.None);
        Assert.Equal("ram", result.Key);
        Assert.True(result.Score > 0);
    }

    [Fact(Timeout = 60_000), Trait("Category", "Manual")]
    public async Task Storage_ProducesPositiveScore()
    {
        var provider = new DefaultBenchmarkProvider();
        var progress = new Progress<BenchmarkPhaseProgress>(_ => { });
        var result = await provider.RunStorageAsync(progress, CancellationToken.None);
        Assert.Equal("storage", result.Key);
        Assert.True(result.Score > 0, $"expected positive storage score, got {result.Score}. detail={result.Detail}");
    }

    [Fact(Timeout = 30_000), Trait("Category", "Manual")]
    public async Task Gpu_SimdProxy_ProducesPositiveScore()
    {
        var provider = new DefaultBenchmarkProvider();
        var progress = new Progress<BenchmarkPhaseProgress>(_ => { });
        var result = await provider.RunGpuAsync(progress, CancellationToken.None);
        Assert.Equal("gpu", result.Key);
        Assert.True(result.Score > 0);
    }

    [Fact(Timeout = 30_000)]
    public async Task Cancellation_AbortsQuickly()
    {
        var provider = new DefaultBenchmarkProvider();
        var progress = new Progress<BenchmarkPhaseProgress>(_ => { });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await provider.RunRamAsync(progress, cts.Token);
        });
    }

    [Fact]
    public void BenchmarkProgressFrame_Roundtrip()
    {
        var frame = new BenchmarkProgressFrame
        {
            RunId = "abc",
            State = "running",
            OverallPercent = 0.5,
            Phase = new BenchmarkPhaseProgress { Phase = "cpu", Percent = 0.5, Detail = "single" },
        };
        var json = System.Text.Json.JsonSerializer.Serialize(
            frame, Qos.Service.Serialization.AppJsonContext.Default.BenchmarkProgressFrame);
        Assert.Contains("\"runId\":\"abc\"", json);
        Assert.Contains("\"phase\":{", json);
    }
}
