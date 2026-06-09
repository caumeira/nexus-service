using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Smart;
using Xunit;

namespace Nexus.Service.Tests.SmartLights;

public class NetworkSendThrottleTests
{
    [Fact]
    public async Task Bursts_coalesce_and_latest_wins()
    {
        using var throttle = new NetworkSendThrottle();
        var sent = new List<LightFrame>();
        var lastSentinel = new TaskCompletionSource();

        const int interval = 80;
        const int n = 30;
        for (var i = 0; i < n; i++)
        {
            var brightness = i / (float)n;
            var isLast = i == n - 1;
            throttle.Submit("dev", new LightFrame(true, (byte)i, 0, 0, brightness), interval, (f, ct) =>
            {
                lock (sent) sent.Add(f);
                if (isLast || f.R == n - 1) lastSentinel.TrySetResult();
                return Task.CompletedTask;
            });
        }

        // The last submitted frame must eventually be delivered.
        await Task.WhenAny(lastSentinel.Task, Task.Delay(2000));
        Assert.True(lastSentinel.Task.IsCompletedSuccessfully, "last frame was never delivered");

        int count;
        LightFrame last;
        lock (sent)
        {
            count = sent.Count;
            last = sent[^1];
        }
        // 30 frames submitted within microseconds, ~80ms spacing → far fewer sends.
        Assert.True(count < n, $"expected coalescing (<{n} sends), got {count}");
        Assert.Equal((byte)(n - 1), last.R);
    }

    [Fact]
    public async Task Remove_stops_the_loop()
    {
        using var throttle = new NetworkSendThrottle();
        var sends = 0;
        throttle.Submit("dev", new LightFrame(true, 1, 0, 0, 1f), 20, (f, ct) =>
        {
            Interlocked.Increment(ref sends);
            return Task.CompletedTask;
        });
        await Task.Delay(60);
        throttle.Remove("dev");
        var after = sends;
        await Task.Delay(120);
        Assert.Equal(after, sends); // no further sends after Remove
    }
}
