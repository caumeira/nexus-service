using System;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Models.Benchmarks;

namespace Qos.Service.Benchmarks;

/// <summary>
/// Cross-platform contract for the four sub-benchmarks. Implementations must
/// honour <paramref name="progress"/> (report percent in the range [0, 1] and
/// the current raw throughput), respect cancellation, and never throw — on
/// failure return a <see cref="BenchmarkSubScore"/> with Score = 0 and a
/// diagnostic in Detail. All implementations are expected to be AOT-safe.
/// </summary>
public interface IBenchmarkProvider
{
    Task<BenchmarkSubScore> RunCpuAsync(IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct);
    Task<BenchmarkSubScore> RunRamAsync(IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct);
    Task<BenchmarkSubScore> RunStorageAsync(IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct);
    Task<BenchmarkSubScore> RunGpuAsync(IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct);
}
