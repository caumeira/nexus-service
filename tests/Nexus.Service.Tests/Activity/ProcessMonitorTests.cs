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

    [Theory]
    [InlineData("processes")]
    [InlineData("monitoring")]
    public async Task FirstSubscriberOnARelevantTopic_ResolvesAPendingWaitViaThePulsePath(string topic)
    {
        var hub = new MultiplexHub();
        var monitor = new ProcessMonitor(hub);

        var waitTask = monitor.WaitForNextSampleAsync(30_000, CancellationToken.None);
        using var sub = hub.AddTestSubscription(topic);

        var completed = await Task.WhenAny(waitTask, Task.Delay(5000));
        Assert.Same(waitTask, completed);
        Assert.True(await waitTask);
    }

    [Fact]
    public async Task FirstSubscriberOnAnUnrelatedTopic_DoesNotResolveAPendingWaitEarly()
    {
        var hub = new MultiplexHub();
        var monitor = new ProcessMonitor(hub);

        var waitTask = monitor.WaitForNextSampleAsync(50, CancellationToken.None);
        using var sub = hub.AddTestSubscription("network");

        Assert.False(await waitTask);
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromTheHub_SoALaterSubscriptionDoesNotResolveAPendingWait()
    {
        var hub = new MultiplexHub();
        var monitor = new ProcessMonitor(hub);
        monitor.Dispose();

        var waitTask = monitor.WaitForNextSampleAsync(50, CancellationToken.None);
        using var sub = hub.AddTestSubscription("processes");

        Assert.False(await waitTask);
    }

    [Fact]
    public void GetProcessMeta_ReturnsNull_BeforeTheBackgroundResolveCompletes()
    {
        var monitor = new ProcessMonitor(new MultiplexHub());
        monitor.SetProcessesForTest(new[]
        {
            new ProcessInfo { Pid = Environment.ProcessId, Name = "app.exe" },
        });

        var meta = monitor.GetProcessMeta("app.exe");

        Assert.Null(meta);
    }

    [Fact]
    public async Task GetProcessMeta_ResolvesPublisherAndSigned_AfterTheBackgroundResolveCompletes()
    {
        var monitor = new ProcessMonitor(new MultiplexHub());
        monitor.SetProcessesForTest(new[]
        {
            new ProcessInfo { Pid = Environment.ProcessId, Name = "app.exe" },
        });

        // First call (from a real broadcast tick) queues the resolve; this
        // waits for the exact same background task instead of polling.
        Assert.Null(monitor.GetProcessMeta("app.exe"));
        await monitor.ResolveMetaForTestAsync("app.exe");

        var meta = monitor.GetProcessMeta("app.exe");

        Assert.NotNull(meta);
        // Signed is always populated ("unknown" off Windows) - Publisher
        // depends on the test host's own binary, so only Signed is asserted.
        Assert.NotNull(meta!.Signed);
    }

    [Fact]
    public async Task GetProcessMeta_SharesOneCacheEntry_WhenTwoNamesResolveToTheSameExePath()
    {
        var monitor = new ProcessMonitor(new MultiplexHub());
        monitor.SetProcessesForTest(new[]
        {
            new ProcessInfo { Pid = Environment.ProcessId, Name = "alias-one.exe" },
        });
        await monitor.ResolveMetaForTestAsync("alias-one.exe");
        var first = monitor.GetProcessMeta("alias-one.exe");

        monitor.SetProcessesForTest(new[]
        {
            new ProcessInfo { Pid = Environment.ProcessId, Name = "alias-two.exe" },
        });
        await monitor.ResolveMetaForTestAsync("alias-two.exe");
        var second = monitor.GetProcessMeta("alias-two.exe");

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task GetProcessMeta_StaysNull_WhenNoLiveProcessMatchesTheName()
    {
        var monitor = new ProcessMonitor(new MultiplexHub());
        monitor.SetProcessesForTest(Array.Empty<ProcessInfo>());

        Assert.Null(monitor.GetProcessMeta("never-seen.exe"));
        await monitor.ResolveMetaForTestAsync("never-seen.exe");

        Assert.Null(monitor.GetProcessMeta("never-seen.exe"));
    }

    [Fact]
    public void ProcessMetaCache_EvictsTheOldestPath_PastTheBound()
    {
        var monitor = new ProcessMonitor(new MultiplexHub());
        for (var i = 0; i < 501; i++)
        {
            monitor.SeedProcessMetaForTest($"/fake/path{i}.exe", new ProcessMeta($"Publisher {i}", "signed"));
        }

        Assert.False(monitor.HasCachedMetaForTest("/fake/path0.exe"));
        Assert.True(monitor.HasCachedMetaForTest("/fake/path500.exe"));
    }

    [Fact]
    public async Task APriorCompletedWait_DoesNotAbsorbALaterPulse()
    {
        // Each call installs its own TaskCompletionSource under _wakeGate, so
        // a prior call whose delay won unpulsed leaves nothing behind that
        // could intercept a pulse meant for a later, still-pending call.
        var hub = new MultiplexHub();
        var monitor = new ProcessMonitor(hub);
        await monitor.WaitForNextSampleAsync(20, CancellationToken.None);

        var waitTask = monitor.WaitForNextSampleAsync(30_000, CancellationToken.None);
        using var sub = hub.AddTestSubscription("processes");

        var completed = await Task.WhenAny(waitTask, Task.Delay(5000));
        Assert.Same(waitTask, completed);
        Assert.True(await waitTask);
    }
}
