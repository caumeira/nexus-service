using BenchmarkDotNet.Running;

namespace Nexus.Service.Benchmarks;

// Explicit entry class (not top-level statements) so it doesn't collide with
// the service's own Program type pulled in via the project reference.
//   dotnet run -c Release -p:BuildWeb=false -- --filter '*'
//   dotnet run -c Release -p:BuildWeb=false -- --filter '*Curve*'
internal static class BenchmarkEntry
{
    private static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(BenchmarkEntry).Assembly).Run(args);
}
