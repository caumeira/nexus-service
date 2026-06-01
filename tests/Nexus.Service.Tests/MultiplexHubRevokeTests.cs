using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Relay;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

/// <summary>
/// Hardening for the phone-session revoke → kick path. Revoking a session whose
/// live client is a relay-bridged <see cref="RelayWebSocket"/> (or any client
/// whose close throws — e.g. a transport already faulted) must NOT fault the
/// revoke: the session removal succeeds, the endpoint returns 200, and the
/// client is still torn down (aborted with the revoked status) so the phone
/// sees the disconnect. The MultiplexHub killswitch semantics stay intact — the
/// kick still closes every client, it just can no longer throw out.
/// </summary>
public sealed class MultiplexHubRevokeTests
{
    private const string ThrowingSession = "sess-throwing";
    private const string RelaySession = "sess-relay";

    [Fact]
    public async Task KickPhoneSessions_DoesNotThrow_WhenClientCloseThrows_AndStillAborts()
    {
        var hub = new MultiplexHub();
        using var cts = new CancellationTokenSource();

        var socket = new ThrowOnCloseWebSocket();
        var loop = hub.HandleClientAsync(
            socket, ThrowingSession, MultiplexHub.ClientTransport.Lan, cts.Token);

        await WaitUntilAsync(() => hub.ClientCount == 1, TimeSpan.FromSeconds(5),
            "client never registered with the hub");

        // The kick must complete cleanly even though CloseAsync throws.
        await hub.KickPhoneSessionsAsync(new[] { ThrowingSession });

        // Fallback abort fired so the client is still torn down (the phone sees
        // the disconnect) rather than left dangling on a failed close.
        Assert.True(socket.CloseAttempted, "CloseRevokedAsync never attempted the orderly close");
        Assert.True(socket.Aborted, "kick did not fall back to Abort after the close threw");

        cts.Cancel();
        socket.UnblockReceive();
        await loop;
    }

    [Fact]
    public async Task KickAllPhone_DoesNotThrow_WithRelayBridgedClientOverFaultedTransport()
    {
        var hub = new MultiplexHub();
        using var cts = new CancellationTokenSource();

        // A relay-bridged client: a real RelayWebSocket whose underlying relay
        // transport both reports a non-Open state AND throws from Abort — the
        // worst case for the revoke path. CloseAsync aborts the transport, so a
        // throwing Abort must not escape and fault the kick.
        var transport = new FaultedThrowingTransport();
        var aeadKey = new byte[32];
        var relayWs = new RelayWebSocket(
            transport, aeadKey, RelayCrypto.DirHostToClient, RelayCrypto.DirClientToHost);

        var loop = hub.HandleClientAsync(
            relayWs, RelaySession, MultiplexHub.ClientTransport.Relay, cts.Token);

        await WaitUntilAsync(() => hub.ClientCount == 1, TimeSpan.FromSeconds(5),
            "relay client never registered with the hub");
        Assert.Equal("relay", hub.GetConnectedTransport(RelaySession));

        // No throw despite the relay transport faulting on Abort.
        await hub.KickAllPhoneAsync();

        Assert.True(transport.AbortCalled, "relay transport was never aborted on revoke");

        // The relayed hub loop tears down (CloseAsync completed the inbound
        // channel), so the session reports disconnected again.
        await WaitUntilAsync(() => hub.GetConnectedTransport(RelaySession) is null,
            TimeSpan.FromSeconds(5), "relay session still reported connected after the kick");

        cts.Cancel();
        await loop;
    }

    [Fact]
    public async Task RevokeSession_RemovesAndReturnsTrue_EvenWhenConnectedClientCloseThrows()
    {
        var store = new InMemoryConfigStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.RemoteControlEnabled = true;
            s.Auth.PanelPhoneSessions = new List<PanelPhoneSessionToken>
            {
                new()
                {
                    Id = ThrowingSession,
                    Hash = "hash",
                    Name = "iPhone",
                    UserAgent = "iPhone",
                    RemoteAddress = "192.168.1.50",
                    CreatedAt = now - 1_000,
                    LastSeenAt = now,
                    ClaimedOverHttps = true,
                },
            };
        });

        var hub = new MultiplexHub();
        var pairing = new PanelPhonePairingService(store, hub) { PublicLinkHost = "" };

        using var cts = new CancellationTokenSource();
        var socket = new ThrowOnCloseWebSocket();
        var loop = hub.HandleClientAsync(
            socket, ThrowingSession, MultiplexHub.ClientTransport.Lan, cts.Token);
        await WaitUntilAsync(() => hub.ClientCount == 1, TimeSpan.FromSeconds(5),
            "client never registered with the hub");

        // The end-to-end revoke must report success (→ HTTP 200) and never
        // propagate the kicked client's close failure.
        var removed = await pairing.RevokeSessionAsync(ThrowingSession);
        Assert.True(removed);

        // Session is gone from the store-backed list.
        var sessions = pairing.GetSessions(connectedCount: 0).Sessions;
        Assert.DoesNotContain(sessions, dto => dto.Id == ThrowingSession);

        // The client was still torn down.
        Assert.True(socket.CloseAttempted);
        Assert.True(socket.Aborted);

        cts.Cancel();
        socket.UnblockReceive();
        await loop;
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
    /// A registered hub client whose orderly <see cref="CloseAsync"/> throws,
    /// standing in for a transport that has faulted between the State check and
    /// the close. Parks on <see cref="ReceiveAsync"/> so it stays registered.
    /// Records whether the kick attempted the close and fell back to Abort.
    /// </summary>
    private sealed class ThrowOnCloseWebSocket : WebSocket
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;

        public bool CloseAttempted { get; private set; }
        public bool Aborted { get; private set; }

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
            CloseAttempted = true;
            throw new WebSocketException("transport faulted mid-close");
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override void Abort()
        {
            Aborted = true;
            _state = WebSocketState.Aborted;
            _release.TrySetResult();
        }

        public override void Dispose() { }
    }

    /// <summary>
    /// Underlying relay transport for a <see cref="RelayWebSocket"/> that is the
    /// pathological case: it reports a faulted state and throws from
    /// <see cref="Abort"/>. RelayWebSocket.CloseAsync calls Abort on it, so this
    /// proves the relay leg swallows its transport error rather than 500ing the
    /// revoke. Sends/receives are inert.
    /// </summary>
    private sealed class FaultedThrowingTransport : WebSocket
    {
        public bool AbortCalled { get; private set; }

        public override void Abort()
        {
            AbortCalled = true;
            throw new ObjectDisposedException(nameof(FaultedThrowingTransport));
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken)
            => throw new WebSocketException("relay transport faulted");

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new WebSocketException("relay transport faulted");

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => throw new WebSocketException("relay transport faulted");

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => throw new WebSocketException("relay transport faulted");

        public override WebSocketState State => WebSocketState.Aborted;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Dispose() { }
    }
}
