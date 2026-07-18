using System.Diagnostics;
using Nexus.Service.Activity;
using Nexus.Service.Sockets;
using Xunit;
using Xunit.Abstractions;

namespace Nexus.Service.Tests.Activity;

/// <summary>
/// Measures SampleWindows' per-tick wall-clock cost with the storage I/O
/// read folded into the existing per-process loop (GetProcessIoCounters
/// reusing the same Process handle TotalProcessorTime/WorkingSet64 already
/// read - no second Process.GetProcesses() enumeration, no second handle
/// open). Runs against every real process on the host so the measured cost
/// reflects the actual production loop, not a synthetic stand-in.
/// Category=Manual: timing is contention-sensitive under concurrent test
/// runs (see the workspace failure log's entries on parallel dotnet test
/// sessions), so this stays out of the default fast gate the same way
/// AppUsageStorageEstimateTests keeps its own projection facts out of it.
/// </summary>
[Trait("Category", "Manual")]
public class ProcessMonitorPerformanceTests
{
    private readonly ITestOutputHelper _output;

    public ProcessMonitorPerformanceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void SampleWindows_StaysUnderBudget_WithStorageIoFoldedIntoTheExistingLoop()
    {
        var monitor = new ProcessMonitor(new MultiplexHub());

        // The first tick only seeds _winPrev/_winStoragePrev (every delta
        // reads 0 - no prior entry yet); the second tick is where every
        // process's CPU and storage delta actually computes, so it is the
        // representative measurement for the production per-tick cost.
        var before = Stopwatch.StartNew();
        monitor.SampleWindows();
        before.Stop();
        var seededCount = monitor.GetProcesses().Count;

        var after = Stopwatch.StartNew();
        monitor.SampleWindows();
        after.Stop();
        var measuredCount = monitor.GetProcesses().Count;

        _output.WriteLine($"before (seeding tick): {before.ElapsedMilliseconds}ms for {seededCount} processes");
        _output.WriteLine($"after (delta tick): {after.ElapsedMilliseconds}ms for {measuredCount} processes");

        // Generous ceiling for a real host's full process list (typically a
        // few hundred processes) - fails loudly on a true regression (a
        // second enumeration pass, a second handle open per process) rather
        // than on ordinary measurement noise.
        Assert.True(after.ElapsedMilliseconds < 500,
            $"SampleWindows took {after.ElapsedMilliseconds}ms for {measuredCount} processes, exceeding the budget");
    }
}
