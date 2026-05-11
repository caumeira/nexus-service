using Qos.Service.Sockets;

namespace Qos.Service.Tests;

public class WebSocketHubTests
{
    [Fact]
    public void ClientCount_StartsAtZero()
    {
        var hub = new WebSocketHub();
        Assert.Equal(0, hub.ClientCount);
    }

    [Fact]
    public void BroadcastBinaryAsync_ReturnsCompletedTask_WhenNoClients()
    {
        // Contract: when there are no clients the hub must not allocate an
        // async state machine per call. The 30 fps lighting loop relies on
        // this short-circuit to stay zero-alloc when nobody is connected.
        var hub = new WebSocketHub();
        var task = hub.BroadcastBinaryAsync(new byte[] { 1, 2, 3 });
        Assert.Same(Task.CompletedTask, task);
    }

    [Fact]
    public void BroadcastBinaryAsync_ROM_ReturnsCompletedTask_WhenNoClients()
    {
        var hub = new WebSocketHub();
        var payload = new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 });
        var task = hub.BroadcastBinaryAsync(payload);
        Assert.Same(Task.CompletedTask, task);
    }
}
