using System.Diagnostics;
using Nexus.Service.Activity;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Activity;

internal sealed class FakeWindowSetProvider : IWindowSetProvider
{
    private readonly HashSet<int> _windowed;
    public FakeWindowSetProvider(params int[] windowedPids) => _windowed = new HashSet<int>(windowedPids);
    public bool IsWindowed(int pid) => _windowed.Contains(pid);
}

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
    public void ResolveExecutablePath_RefreshesPath_WhenTheCachedPidDiesAndANewProcessReusesTheName()
    {
        var monitor = new ProcessMonitor(new MultiplexHub());

        // Seed the cache as if an earlier tick resolved "app.exe" to some
        // pid that has since exited.
        const int stalePid = 999_999;
        monitor.SeedResolvedPathForTest("app.exe", "/some/old/path/app.exe", stalePid);

        // The next sampling tick's live-pid set no longer contains that
        // pid - the same sweep SampleWindows/SampleMacOs run every tick.
        monitor.PruneDeadPathCacheEntries(new HashSet<int>());

        // A different real process now carries the same aggregate name.
        monitor.SetProcessesForTest(new[]
        {
            new ProcessInfo { Pid = Environment.ProcessId, Name = "app.exe" },
        });

        var path = monitor.ResolveExecutablePath("app.exe");

        Assert.Equal(Process.GetCurrentProcess().MainModule?.FileName, path);
        Assert.NotEqual("/some/old/path/app.exe", path);
    }

    [Fact]
    public void PruneDeadPathCacheEntries_LeavesAnEntryAlone_WhileItsPidIsStillLive()
    {
        var monitor = new ProcessMonitor(new MultiplexHub());
        monitor.SeedResolvedPathForTest("app.exe", "/some/path/app.exe", Environment.ProcessId);

        monitor.PruneDeadPathCacheEntries(new HashSet<int> { Environment.ProcessId });

        monitor.SetProcessesForTest(Array.Empty<ProcessInfo>());
        var path = monitor.ResolveExecutablePath("app.exe");

        Assert.Equal("/some/path/app.exe", path);
    }

    [Fact]
    public void AnchorFirstSeenMs_ReturnsTheSameValue_OnRepeatedCallsForTheSamePid()
    {
        var monitor = new ProcessMonitor(new MultiplexHub());
        var first = monitor.AnchorFirstSeenMs(4242, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var second = monitor.AnchorFirstSeenMs(4242, new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc));

        Assert.Equal(first, second);
    }

    [Fact]
    public void AnchorFirstSeenMs_AnchorsOnTheFirstObservedTimestamp()
    {
        var monitor = new ProcessMonitor(new MultiplexHub());
        var observedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var anchored = monitor.AnchorFirstSeenMs(4242, observedAt);

        Assert.Equal(new DateTimeOffset(observedAt).ToUnixTimeMilliseconds(), anchored);
    }

    [Fact]
    public void AnchorFirstSeenMs_TracksEachPidIndependently()
    {
        var monitor = new ProcessMonitor(new MultiplexHub());
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

        var a = monitor.AnchorFirstSeenMs(1, t1);
        var b = monitor.AnchorFirstSeenMs(2, t2);

        Assert.NotEqual(a, b);
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
    public void SampleWindows_SetsHasWindowTrue_ForAPidTheWindowSetProviderReports()
    {
        // The service process cannot see Session-1 windows directly (that is
        // the whole reason IWindowSetProvider exists) - HasWindow must come
        // from the injected provider, never from a live Win32 call here.
        var windowSet = new FakeWindowSetProvider(Environment.ProcessId);
        var monitor = new ProcessMonitor(new MultiplexHub(), windowSet);

        monitor.SampleWindows();

        var self = monitor.GetProcesses().Single(p => p.Pid == Environment.ProcessId);
        Assert.True(self.HasWindow);
    }

    [Fact]
    public void SampleWindows_SetsHasWindowFalse_WhenNoWindowSetProviderIsRegistered()
    {
        var monitor = new ProcessMonitor(new MultiplexHub());

        monitor.SampleWindows();

        var self = monitor.GetProcesses().SingleOrDefault(p => p.Pid == Environment.ProcessId);
        Assert.NotNull(self);
        Assert.False(self!.HasWindow);
    }

    [Fact]
    public void SampleWindows_SetsHasWindowFalse_ForAPidTheWindowSetProviderDoesNotReport()
    {
        var windowSet = new FakeWindowSetProvider(999_999); // some other pid, not this test process
        var monitor = new ProcessMonitor(new MultiplexHub(), windowSet);

        monitor.SampleWindows();

        var self = monitor.GetProcesses().Single(p => p.Pid == Environment.ProcessId);
        Assert.False(self.HasWindow);
    }

    [Fact]
    public async Task GetProcessMeta_DoesNotReattemptResolve_WithinTheFailureCooldown()
    {
        var monitor = new ProcessMonitor(new MultiplexHub());
        monitor.SetProcessesForTest(new[]
        {
            new ProcessInfo { Pid = 999_999, Name = "unresolvable.exe" }, // not a live pid: path never resolves
        });

        await monitor.ResolveMetaForTestAsync("unresolvable.exe");
        Assert.Equal(1, monitor.MetaResolveAttemptsForTest);

        Assert.Null(monitor.GetProcessMeta("unresolvable.exe"));
        await monitor.ResolveMetaForTestAsync("unresolvable.exe");

        Assert.Equal(1, monitor.MetaResolveAttemptsForTest);
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

    // Fixture text mirrors the real /proc/pid/io field order (rchar, wchar,
    // syscr, syscw, read_bytes, write_bytes, cancelled_write_bytes).
    private const string SampleProcIo =
        "rchar: 123456\n" +
        "wchar: 654321\n" +
        "syscr: 10\n" +
        "syscw: 12\n" +
        "read_bytes: 4096\n" +
        "write_bytes: 8192\n" +
        "cancelled_write_bytes: 0\n";

    [Fact]
    public void TryParseLinuxIoBytes_ReadsReadBytesAndWriteBytes_NotRcharWchar()
    {
        Assert.True(ProcessMonitor.TryParseLinuxIoBytes(SampleProcIo, out var read, out var written));
        Assert.Equal(4096, read);
        Assert.Equal(8192, written);
    }

    [Fact]
    public void TryParseLinuxIoBytes_ReturnsFalse_WhenNeitherFieldIsPresent()
    {
        Assert.False(ProcessMonitor.TryParseLinuxIoBytes("rchar: 1\nwchar: 2\n", out var read, out var written));
        Assert.Equal(0, read);
        Assert.Equal(0, written);
    }

    [Fact]
    public void TryParseLinuxIoBytes_ReadsWhicheverFieldIsPresent_WhenOnlyOneIs()
    {
        Assert.True(ProcessMonitor.TryParseLinuxIoBytes("read_bytes: 100\n", out var read, out var written));
        Assert.Equal(100, read);
        Assert.Equal(0, written);
    }
}
