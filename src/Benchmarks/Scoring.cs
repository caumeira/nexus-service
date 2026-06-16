using System;

namespace Nexus.Service.Benchmarks;

internal static class Scoring
{
    // v2.1-2026.06: primesieve / clpeak+vkpeak / STREAM / DiskSpd
    // Reference machine: Ryzen 7600 / RTX 4060 / DDR5-6000 CL30 / PCIe 4 NVMe
    // Bump on any tool, invocation, or baseline change so the leaderboard
    // partitions. The CPU baseline is tied to the all-core sieve size (4e11);
    // changing it shifts the rate and requires a new version.
    public const string ScoringVersion = "v2.1-2026.06";

    public const double BaselineCpuPrimesPerSec = 2_000_000_000d;
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

}
