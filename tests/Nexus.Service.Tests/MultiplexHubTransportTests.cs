using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

/// <summary>
/// Per-session transport reporting on the hub. A relay-bridged client (the path
/// <c>RelayConnectionService</c> drives) must report
/// <see cref="MultiplexHub.GetConnectedTransport"/> == "relay"; a direct LAN /ws
/// client must report "lan"; an authorized-but-disconnected session reports null;
/// and a session that is both bridged and on LAN prefers "relay".
/// </summary>
public sealed class MultiplexHubTransportTests
{
    private const string RelaySession = "sess-relay";
    private const string LanSession = "sess-lan";

    [Fact]
    public async Task GetConnectedTransport_ReportsRelayLanAndNull()
    {
        var hub = new MultiplexHub();

        using var relayCts = new CancellationTokenSource();
        using var lanCts = new CancellationTokenSource();
        var relaySocket = new BlockingWebSocket();
        var lanSocket = new BlockingWebSocket();

        // Relay bridge: same call RelayConnectionService makes on peer-up.
        var relayLoop = hub.HandleClientAsync(
            relaySocket, RelaySession, MultiplexHub.ClientTransport.Relay, relayCts.Token);
        // LAN /ws client: the WebSocketRoutes default transport.
        var lanLoop = hub.HandleClientAsync(
            lanSocket, LanSession, MultiplexHub.ClientTransport.Lan, lanCts.Token);

        await WaitUntilAsync(() => hub.ClientCount == 2, TimeSpan.FromSeconds(5),
            "both clients never registered with the hub");

        Assert.Equal("relay", hub.GetConnectedTransport(RelaySession));
        Assert.Equal("lan", hub.GetConnectedTransport(LanSession));
        // Authorized session with no live client → null.
        Assert.Null(hub.GetConnectedTransport("sess-offline"));
        Assert.Null(hub.GetConnectedTransport(""));

        // Dropping the relay client (relay-off / kick tears down the host socket)
        // makes its session report null again.
        relayCts.Cancel();
        relaySocket.UnblockReceive();
        await WaitUntilAsync(() => hub.GetConnectedTransport(RelaySession) is null,
            TimeSpan.FromSeconds(5), "relay session still reported connected after the bridge closed");

        Assert.Equal("lan", hub.GetConnectedTransport(LanSession));

        lanCts.Cancel();
        lanSocket.UnblockReceive();
        await relayLoop;
        await lanLoop;
    }

    [Fact]
    public async Task GetConnectedTransport_PrefersRelayWhenSessionHasBoth()
    {
        var hub = new MultiplexHub();

        using var cts = new CancellationTokenSource();
        var lanSocket = new BlockingWebSocket();
        var relaySocket = new BlockingWebSocket();

        const string shared = "sess-both";
        var lanLoop = hub.HandleClientAsync(
            lanSocket, shared, MultiplexHub.ClientTransport.Lan, cts.Token);
        var relayLoop = hub.HandleClientAsync(
            relaySocket, shared, MultiplexHub.ClientTransport.Relay, cts.Token);

        await WaitUntilAsync(() => hub.ClientCount == 2, TimeSpan.FromSeconds(5),
            "both clients never registered with the hub");

        // Relay wins over LAN for the same session id.
        Assert.Equal("relay", hub.GetConnectedTransport(shared));

        cts.Cancel();
        lanSocket.UnblockReceive();
        relaySocket.UnblockReceive();
        await lanLoop;
        await relayLoop;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, string failMessage)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate())
                return;
            await Task.Delay(20);
        }
        Assert.Fail(failMessage);
    }

    /// <summary>
    /// Minimal in-memory WebSocket: stays <see cref="WebSocketState.Open"/> and
    /// parks on <see cref="ReceiveAsync"/> until cancelled or
    /// <see cref="UnblockReceive"/>, so the hub's receive loop keeps the client
    /// registered for the duration of the assertions. Sends are no-ops.
    /// </summary>
    private sealed class BlockingWebSocket : WebSocket
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;

        public void UnblockReceive() => _release.TrySetResult();

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            using var reg = cancellationToken.Register(() => _release.TrySetResult());
            await _release.Task.ConfigureAwait(false);
            _state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() { }
    }
}
