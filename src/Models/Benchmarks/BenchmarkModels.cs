using System.Collections.Generic;

namespace Qos.Service.Models.Benchmarks;

public sealed class HardwareIdentity
{
    public string CpuModel { get; set; } = "";
    public List<string> GpuModels { get; set; } = new();
    public string RamModel { get; set; } = "";
    public long RamBytes { get; set; }
    public string StorageModel { get; set; } = "";
    public int LogicalCores { get; set; }
    public string Os { get; set; } = "";
    public string Architecture { get; set; } = "";
}

public sealed class BenchmarkSubScore
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public double Score { get; set; }
    public double RawValue { get; set; }
    public string RawUnit { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class BenchmarkPhaseProgress
{
    public string Phase { get; set; } = "";
    public double Percent { get; set; }
    public string Detail { get; set; } = "";
    public double CurrentRaw { get; set; }
    public string CurrentUnit { get; set; } = "";
}

public sealed class BenchmarkProgressFrame
{
    public string RunId { get; set; } = "";
    public string State { get; set; } = "";
    public double OverallPercent { get; set; }
    public BenchmarkPhaseProgress Phase { get; set; } = new();
    public List<BenchmarkSubScore> CompletedSubScores { get; set; } = new();
}

public sealed class BenchmarkResult
{
    public string RunId { get; set; } = "";
    public string State { get; set; } = "";
    public long StartedAt { get; set; }
    public long FinishedAt { get; set; }
    public HardwareIdentity Hardware { get; set; } = new();
    public double Composite { get; set; }
    public BenchmarkSubScore Cpu { get; set; } = new();
    public BenchmarkSubScore Gpu { get; set; } = new();
    public BenchmarkSubScore Ram { get; set; } = new();
    public BenchmarkSubScore Storage { get; set; } = new();
    public string Error { get; set; } = "";
}

public sealed class StartBenchmarkResponse
{
    public string RunId { get; set; } = "";
    public bool Started { get; set; }
    public string Error { get; set; } = "";
}

public sealed class StartBenchmarkBody
{
    public bool IncludeGpu { get; set; } = true;
}
