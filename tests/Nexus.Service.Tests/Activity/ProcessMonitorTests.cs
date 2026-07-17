using System.Diagnostics;
using Nexus.Service.Activity;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class ProcessMonitorTests
{
    [Fact]
    public void ResolveExecutablePath_ReturnsNull_WhenNoLiveProcessMatchesTheName()
    {
        var monitor = new ProcessMonitor(new MultiplexHub());
        monitor.SetProcessesForTest(new[] { new ProcessInfo { Pid = 1, Name = "other.exe" } });

        var path = monitor.ResolveExecutablePath("never-seen.exe");

        Assert.Null(path);
    }

    [Fact]
    public void ResolveExecutablePath_ResolvesTheRealPath_ForALivePid()
    {
        var monitor = new ProcessMonitor(new MultiplexHub());
        var expected = Process.GetCurrentProcess().MainModule?.FileName;
        monitor.SetProcessesForTest(new[]
        {
            new ProcessInfo { Pid = Environment.ProcessId, Name = "app.exe" },
        });

        var path = monitor.ResolveExecutablePath("app.exe");

        Assert.Equal(expected, path);
    }

    [Fact]
    public void ResolveExecutablePath_PicksTheNewestInstance_WhenSeveralPidsShareAName()
    {
        var monitor = new ProcessMonitor(new MultiplexHub());
        monitor.SetProcessesForTest(new[]
        {
            new ProcessInfo { Pid = 999_999, Name = "app.exe", StartedAtMs = 1000 }, // not live: resolution fails, falls through
            new ProcessInfo { Pid = Environment.ProcessId, Name = "app.exe", StartedAtMs = 9000 },
        });

        var path = monitor.ResolveExecutablePath("app.exe");

        Assert.Equal(Process.GetCurrentProcess().MainModule?.FileName, path);
    }

    [Fact]
    public void ResolveExecutablePath_CachesASuccessfulResolution_AcrossCalls()
    {
        var monitor = new ProcessMonitor(new MultiplexHub());
        monitor.SetProcessesForTest(new[] { new ProcessInfo { Pid = Environment.ProcessId, Name = "app.exe" } });
        var first = monitor.ResolveExecutablePath("app.exe");

        // Even after the live snapshot no longer has a matching name, the
        // cached resolution still answers - a repeat request for a popular
        // app must not require it to still be in the latest snapshot.
        monitor.SetProcessesForTest(Array.Empty<ProcessInfo>());
        var second = monitor.ResolveExecutablePath("app.exe");

        Assert.Equal(first, second);
        Assert.NotNull(second);
    }

    [Fact]
    public void SetDemand_KeepsHasSubscribersLogicIndependentOfHubTopics()
    {
        // No direct HasSubscribers accessor to assert on, but SetDemand must
        // not throw and must be idempotent per source id.
        var monitor = new ProcessMonitor(new MultiplexHub());

        monitor.SetDemand("app-usage-history", true);
        monitor.SetDemand("app-usage-history", true);
        monitor.SetDemand("app-usage-history", false);
        monitor.SetDemand("app-usage-history", false);
    }
}
