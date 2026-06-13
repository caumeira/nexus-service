using BenchmarkDotNet.Attributes;
using Nexus.Service.Cooling;
using Nexus.Service.Persistence;

namespace Nexus.Service.Benchmarks;

/// <summary>
/// Curve evaluation runs once per fan channel per cooling tick (1 Hz). Cheap per
/// call, but <see cref="CurveEngine.EvaluateGraph"/> re-sorts the points list on
/// every call | this measures whether that sort is worth removing.
/// </summary>
[MemoryDiagnoser]
[InProcess]
public class CurveEvalBenchmarks
{
    private readonly GraphCurveData _graph = Payloads.GraphCurve(8);
    private readonly LinearCurveData _linear = new() { MinTemp = 30, MaxTemp = 80, MinSpeed = 20, MaxSpeed = 100 };
    private readonly FlatCurveData _flat = new() { Speed = 55 };

    [Benchmark]
    public double? EvaluateGraph() => CurveEngine.EvaluateGraph(_graph, 55f);

    [Benchmark]
    public double? EvaluateLinear() => CurveEngine.EvaluateLinear(_linear, 55f);

    [Benchmark]
    public double? EvaluateFlat() => CurveEngine.EvaluateFlat(_flat);
}
