using System;

namespace Qos.Service.Benchmarks;

internal static class Scoring
{
    // Weights sum to 1.0. See plans/benchmark-system.md for rationale.
    public const double WeightCpu = 0.30;
    public const double WeightGpu = 0.35;
    public const double WeightRam = 0.15;
    public const double WeightStorage = 0.20;

    // Reference raw numbers that normalise to ~1000 points. These came from a
    // spot-sample on a mid-range 2023/2024 build (Ryzen 7600 / RTX 4060-class /
    // DDR5-6000 CL30 / PCIe 4.0 NVMe). The raw values are intentionally stable
    // so scores stay comparable across app versions — if the reference needs
    // to change, bump a schema version so the leaderboard knows to partition.
    public const double RefCpuHashesPerSec = 2_500_000_000d;
    public const double RefGpuGflops = 400d;
    public const double RefRamGbPerSec = 35d;
    public const double RefStorageComposite = 3_000d; // MB/s equivalent

    public static double Normalize(double raw, double reference)
    {
        if (reference <= 0 || raw <= 0)
            return 0;
        return Math.Round((raw / reference) * 1000, 2);
    }

    public static double WeightedGeoMean(double cpu, double gpu, double ram, double storage)
    {
        double Safe(double v) => v <= 0 ? 1 : v;
        double logSum =
            WeightCpu * Math.Log(Safe(cpu)) +
            WeightGpu * Math.Log(Safe(gpu)) +
            WeightRam * Math.Log(Safe(ram)) +
            WeightStorage * Math.Log(Safe(storage));
        return Math.Round(Math.Exp(logSum), 2);
    }
}
