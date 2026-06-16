using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Benchmarks;
using Nexus.Service.Benchmarks.Providers;
using Nexus.Service.Models.Benchmarks;

namespace Nexus.Service.Tests;

public class BenchmarkTests
{
    [Fact]
    public void Scoring_Score_ZeroReference_ReturnsZero()
    {
        Assert.Equal(0, Scoring.Score(100, 0));
    }

    [Fact]
    public void Scoring_Score_AtBaseline_ReturnsThousand()
    {
        Assert.Equal(1000, Scoring.Score(Scoring.BaselineCpuPrimesPerSec, Scoring.BaselineCpuPrimesPerSec));
    }

    [Fact]
    public void Scoring_Composite_AllThousand_ReturnsThousand()
    {
        var result = Scoring.Composite(1000, 1000, 1000, 1000);
        Assert.InRange(result, 999, 1001);
    }

    [Fact]
    public void Scoring_Composite_WeakStorageHurtsComposite()
    {
        var balanced = Scoring.Composite(1000, 1000, 1000, 1000);
        var weakStorage = Scoring.Composite(1000, 1000, 1000, 100);
        Assert.True(weakStorage < balanced * 0.75);
    }

    [Fact]
    public void Scoring_ScoringVersion_NotEmpty()
    {
        Assert.False(string.IsNullOrEmpty(Scoring.ScoringVersion));
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
            frame, Nexus.Service.Serialization.AppJsonContext.Default.BenchmarkProgressFrame);
        Assert.Contains("\"runId\":\"abc\"", json);
        Assert.Contains("\"phase\":{", json);
    }

    [Fact]
    public void ParseStreamTriad_ValidOutput_ParsesGbPerSec()
    {
        const string output = @"Function    Best Rate MB/s  Avg time     Min time     Max time
Copy:           52000.0     0.006154     0.006154     0.006155
Scale:          51000.0     0.006278     0.006278     0.006280
Add:            53000.0     0.009056     0.009056     0.009056
Triad:          54321.0     0.009055     0.009055     0.009055";

        double result = ExternalToolBenchmarkProvider.ParseStreamTriad(output);
        Assert.InRange(result, 54.3, 54.4);
    }

    [Fact]
    public void ParseDiskSpdXml_ValidXml_ParsesMbPerSec()
    {
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<Results>
  <TimeSpan>
    <TestTimeSeconds>15</TestTimeSeconds>
    <Thread>
      <Target>
        <BytesCount>47185920000</BytesCount>
        <IOCount>45000</IOCount>
      </Target>
    </Thread>
    <AverageLatencyMilliseconds>0.33</AverageLatencyMilliseconds>
  </TimeSpan>
</Results>";

        var (seqMbPerSec, iops, latMs) = ExternalToolBenchmarkProvider.ParseDiskSpdXml(xml);
        Assert.True(seqMbPerSec > 2000, $"expected >2000 MB/s, got {seqMbPerSec}");
        Assert.True(iops > 0, $"expected positive IOPS, got {iops}");
        Assert.InRange(latMs, 0.3, 0.4);
    }

    [Fact]
    public void ParsePrimesPerSec_RealOutput_ParsesCorrectly()
    {
        const string output = "Primes: 455,052,511\nSeconds: 2.123";
        double result = ExternalToolBenchmarkProvider.ParsePrimesPerSec(output);
        Assert.InRange(result, 214_000_000d, 215_000_000d);
    }

    [Fact]
    public void ParsePrimesPerSec_NoMatch_ReturnsZero()
    {
        double result = ExternalToolBenchmarkProvider.ParsePrimesPerSec("some unrelated output");
        Assert.Equal(0, result);
    }
}
