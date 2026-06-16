using System;

namespace Nexus.Service.Benchmarks;

internal static class Scoring
{
    // v2-2026.06: primesieve / clpeak+vkpeak / STREAM / DiskSpd
    // Reference machine: Ryzen 7600 / RTX 4060 / DDR5-6000 CL30 / PCIe 4 NVMe
    public const string ScoringVersion = "v2-2026.06";

    public const double BaselineCpuPrimesPerSec = 2_800_000_000d;
    public const double BaselineGpuGflops = 10_000d;
    public const double BaselineRamGbPerSec = 35d;
    public const double BaselineStorageMbPerSec = 3_000d;

    public static double Score(double raw, double baseline)
    {
        if (baseline <= 0 || raw <= 0)
            return 0;
        return Math.Round(raw / baseline * 1000d, 1);
    }

    public static double Composite(double cpuScore, double gpuScore, double ramScore, double storageScore)
    {
        static double Safe(double v) => v <= 0 ? 1 : v;
        double logSum =
            Math.Log(Safe(cpuScore)) +
            Math.Log(Safe(gpuScore)) +
            Math.Log(Safe(ramScore)) +
            Math.Log(Safe(storageScore));
        return Math.Round(Math.Exp(logSum / 4d), 1);
    }

    // Compatibility shims -- delegates to Score()
    public static double Normalize(double raw, double reference) => Score(raw, reference);

    // Compatibility shim -- delegates to Composite()
    public static double WeightedGeoMean(double cpu, double gpu, double ram, double storage)
        => Composite(cpu, gpu, ram, storage);

    // Old reference constants kept for test compatibility only
    public const double RefCpuHashesPerSec = BaselineCpuPrimesPerSec;
    public const double RefGpuGflops = BaselineGpuGflops;
    public const double RefRamGbPerSec = BaselineRamGbPerSec;
    public const double RefStorageComposite = BaselineStorageMbPerSec;
}
