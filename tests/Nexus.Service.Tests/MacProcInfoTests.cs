using System.Diagnostics;
using Nexus.Service.Platform.Mac;
using Xunit;

namespace Nexus.Service.Tests;

public class MacProcInfoTests
{
    // PROC_PIDTASKINFO CPU counters are mach absolute-time ticks; the
    // converted value must agree with the runtime's own (timebase-correct)
    // process CPU accounting. On Apple Silicon an unconverted read is a few
    // percent of the true value, so a dropped conversion fails this by an
    // order of magnitude. The runtime value is read before AND after the
    // task-info read: both counters are monotonic, so the converted value
    // must land inside the bracket however the suite's parallel workers
    // deschedule this thread between reads.
    [MacOnlyFact]
    public void MachTicksToNs_AgreesWithRuntimeProcessCpuTime()
    {
        // Burn enough CPU that the reads below sit on a non-trivial total.
        var sw = Stopwatch.StartNew();
        double x = 1.0;
        while (sw.ElapsedMilliseconds < 100) { x = x * 1.0000001 + 1.0; }
        Assert.True(x > 0);

        var before = Process.GetCurrentProcess().TotalProcessorTime;
        Assert.True(MacProcInfo.TryGetTaskInfo(Environment.ProcessId, out var ti));
        var after = Process.GetCurrentProcess().TotalProcessorTime;

        var converted = TimeSpan.FromMilliseconds(
            MacProcInfo.MachTicksToNs(ti.TotalUser + ti.TotalSystem) / 1_000_000.0);

        // Slack covers the two sources' differing scopes, not scheduling.
        Assert.True(converted >= before * 0.5,
            $"converted {converted} vs runtime bracket [{before}, {after}]");
        Assert.True(converted <= after + TimeSpan.FromMilliseconds(100),
            $"converted {converted} vs runtime bracket [{before}, {after}]");
    }
}
