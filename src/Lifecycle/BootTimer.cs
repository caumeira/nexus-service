using System.Diagnostics;

namespace Nexus.Service.Lifecycle;

// Temporary boot-trace instrumentation. Prints `[boot] +<elapsed>ms (+<delta>ms) <label>`
// per phase so we can attribute the ~2-3s gap between process start and
// the first `[nexus-service] listening` log line on cold boots. Sw starts
// at static-ctor time, which fires the first time the type is touched —
// i.e. the very first call to BootTimer.Mark() near the top of Program.cs.
//
// Remove the call sites once boot time is back under the desired budget.
internal static class BootTimer
{
    private static readonly Stopwatch Sw = Stopwatch.StartNew();
    private static long _lastMs;
    private static readonly object Lock = new();

    public static void Mark(string label)
    {
        long now = Sw.ElapsedMilliseconds;
        long delta;
        lock (Lock)
        {
            delta = now - _lastMs;
            _lastMs = now;
        }
        Console.WriteLine($"[boot] +{now,5}ms (+{delta,4}ms) {label}");
    }

    public static long ElapsedMs => Sw.ElapsedMilliseconds;
}
