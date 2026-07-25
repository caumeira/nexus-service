using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class GpuProcessMonitorTests
{
    [Fact]
    public async Task FirstSubscriberOnGpuProcessesTopic_ResolvesAPendingWaitViaThePulsePath()
    {
        var hub = new MultiplexHub();
        var monitor = new GpuProcessMonitor(hub, new InMemoryConfigStore());

        var waitTask = monitor.WaitForNextSampleAsync(30_000, CancellationToken.None);
        using var sub = hub.AddTestSubscription("gpu-processes");

        var completed = await Task.WhenAny(waitTask, Task.Delay(5000));
        Assert.Same(waitTask, completed);
        Assert.True(await waitTask);
    }

    [Fact]
    public async Task FirstSubscriberOnAnUnrelatedTopic_DoesNotResolveAPendingWaitEarly()
    {
        var hub = new MultiplexHub();
        var monitor = new GpuProcessMonitor(hub, new InMemoryConfigStore());

        var waitTask = monitor.WaitForNextSampleAsync(50, CancellationToken.None);
        using var sub = hub.AddTestSubscription("processes");

        Assert.False(await waitTask);
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromTheHub_SoALaterSubscriptionDoesNotResolveAPendingWait()
    {
        var hub = new MultiplexHub();
        var monitor = new GpuProcessMonitor(hub, new InMemoryConfigStore());
        monitor.Dispose();

        var waitTask = monitor.WaitForNextSampleAsync(50, CancellationToken.None);
        using var sub = hub.AddTestSubscription("gpu-processes");

        Assert.False(await waitTask);
    }

    [Fact]
    public async Task APriorCompletedWait_DoesNotAbsorbALaterPulse()
    {
        var hub = new MultiplexHub();
        var monitor = new GpuProcessMonitor(hub, new InMemoryConfigStore());
        await monitor.WaitForNextSampleAsync(20, CancellationToken.None);

        var waitTask = monitor.WaitForNextSampleAsync(30_000, CancellationToken.None);
        using var sub = hub.AddTestSubscription("gpu-processes");

        var completed = await Task.WhenAny(waitTask, Task.Delay(5000));
        Assert.Same(waitTask, completed);
        Assert.True(await waitTask);
    }
}
