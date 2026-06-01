using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Service.Persistence;
using Nexus.Service.Relay;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

/// <summary>
/// In-process integration test for the relay client. Stands up a minimal fake
/// relay (a loopback <see cref="HttpListener"/> WebSocket server implementing
/// just enough of the wire protocol: accept the host hello, send peer-up,
/// forward a sealed client frame, read host frames) and proves:
///   • the service registers a host socket with the correct rid,
///   • on peer-up a sealed client frame round-trips INTO the MultiplexHub
///     (a `sub` command is processed),
///   • a hub broadcast round-trips back OUT to the relay as a sealed frame,
///   • KickAllPhoneAsync closes the relayed session.
/// </summary>
public sealed class RelayConnectionServiceTests
{
    private const string Token = "test-session-token-0123456789";
    private const string SessionId = "sess-abc";
    private const string Topic = "relay-roundtrip-topic";

    [Fact]
    public async Task PeerUp_RoundTripsFrameIntoHub_AndKickClosesIt()
    {
        using var relay = new FakeRelay();
        await relay.StartAsync();

        var relayRoot = RelayCrypto.DeriveRelayRoot(Token);
        var expectedRid = RelayCrypto.DeriveRid(relayRoot);
        relay.RuntimeRid = expectedRid;

        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.RemoteControlEnabled = true;
            s.Auth.RelayEnabled = true;
            s.Auth.PanelPhoneSessions = new List<PanelPhoneSessionToken>
            {
                new()
                {
                    Id = SessionId,
                    Hash = "hash-placeholder",
                    RelayKey = Convert.ToBase64String(relayRoot),
                    Name = "iPhone",
                    UserAgent = "iPhone",
                    RemoteAddress = "192.168.1.50",
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    LastSeenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ClaimedOverHttps = true,
                },
            };
        });

        var hub = new MultiplexHub();
        var pairing = new Nexus.Service.Panel.PanelPhonePairingService(store, hub) { PublicLinkHost = "" };

        using var service = new RelayConnectionService(
            NullLogger<RelayConnectionService>.Instance, pairing, store, hub,
            RelayTestHelpers.InertHttpDispatcher())
        {
            Endpoint = relay.Uri,
        };

        // Capture host→client broadcasts that reach the relay.
        var broadcastReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        relay.OnHostFrame = frame => broadcastReceived.TrySetResult(frame);

        await service.StartAsync(CancellationToken.None);

        // 1) The host socket connects + sends the hello with the right rid.
        var hello = await relay.WaitForHelloAsync();
        Assert.Equal(1, hello.GetProperty("v").GetInt32());
        Assert.Equal("host", hello.GetProperty("role").GetString());
        Assert.Equal(expectedRid, hello.GetProperty("rid").GetString());

        // 2) Simulate a client peering up: pick a connSalt, derive the matching
        //    client aeadKey, send peer-up to the host.
        var connSalt = new byte[RelayCrypto.ConnSaltLength];
        for (var i = 0; i < connSalt.Length; i++) connSalt[i] = (byte)(i + 0x10);
        var clientKey = RelayCrypto.DeriveAeadKey(relayRoot, connSalt);
        await relay.SendPeerUpAsync(connSalt);

        // 3) Client → host: seal a `sub` command (dir=2) and forward it as BINARY.
        var subCommand = Encoding.UTF8.GetBytes($"{{\"sub\":[\"{Topic}\"]}}");
        var clientFrame = RelayCrypto.Seal(clientKey, RelayCrypto.DirClientToHost, counter: 0, subCommand);
        await relay.ForwardToHostAsync(clientFrame);

        // The hub must process the sub command → the topic gains a subscriber.
        await WaitUntilAsync(() => hub.TopicHasSubscribers(Topic), TimeSpan.FromSeconds(5),
            "relayed `sub` command never reached the hub");

        // 4) Host → client: broadcast on the topic; the relayed socket seals it
        //    (dir=1) and writes it back to the relay, which decrypts with the
        //    client key and recovers the exact plaintext.
        var payload = Encoding.UTF8.GetBytes($"{{\"t\":\"{Topic}\",\"d\":42}}");
        await hub.BroadcastTopicAsync(Topic, payload);

        var hostFrame = await broadcastReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var (dir, counter, plaintext) = RelayCrypto.Open(clientKey, hostFrame);
        Assert.Equal(RelayCrypto.DirHostToClient, dir);
        Assert.Equal(0ul, counter);
        Assert.Equal(payload, plaintext);

        // 5) Killswitch parity: the relayed session carries the phoneSessionId,
        //    so KickAllPhoneAsync closes it; the topic loses its subscriber.
        await hub.KickAllPhoneAsync();
        await WaitUntilAsync(() => !hub.TopicHasSubscribers(Topic), TimeSpan.FromSeconds(5),
            "KickAllPhoneAsync did not close the relayed session");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RelayDisabled_DoesNotConnect()
    {
        using var relay = new FakeRelay();
        await relay.StartAsync();

        var relayRoot = RelayCrypto.DeriveRelayRoot(Token);
        relay.RuntimeRid = RelayCrypto.DeriveRid(relayRoot);
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.RemoteControlEnabled = true;
            s.Auth.RelayEnabled = false; // opt-in OFF
            s.Auth.PanelPhoneSessions = new List<PanelPhoneSessionToken>
            {
                new()
                {
                    Id = SessionId, Hash = "h", RelayKey = Convert.ToBase64String(relayRoot),
                    Name = "iPhone", UserAgent = "iPhone", RemoteAddress = "10.0.0.2",
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    LastSeenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ClaimedOverHttps = true,
                },
            };
        });

        var hub = new MultiplexHub();
        var pairing = new Nexus.Service.Panel.PanelPhonePairingService(store, hub) { PublicLinkHost = "" };
        using var service = new RelayConnectionService(
            NullLogger<RelayConnectionService>.Instance, pairing, store, hub,
            RelayTestHelpers.InertHttpDispatcher())
        {
            Endpoint = relay.Uri,
        };

        await service.StartAsync(CancellationToken.None);
        var connected = await relay.WaitForConnectionAsync(TimeSpan.FromSeconds(1));
        Assert.False(connected, "service connected to the relay while the opt-in was off");

        // Now flip the opt-in on: the OnChanged signal should bring up the socket
        // with no poll loop.
        pairing.SetRelayEnabled(true);
        var hello = await relay.WaitForHelloAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("host", hello.GetProperty("role").GetString());

        await service.StopAsync(CancellationToken.None);
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
    /// Loopback fake relay over an HttpListener. The service now opens TWO host
    /// links per session (runtime rid + rid_http), so the relay accepts MULTIPLE
    /// connections and routes by the hello's rid. The test binds peer-up /
    /// forward / host-frame capture to a chosen <see cref="RuntimeRid"/> (the rid
    /// derived from the session's relayRoot), so the rid_http leg simply
    /// connects + sits idle and never perturbs the runtime assertions.
    /// </summary>
    private sealed class FakeRelay : IDisposable
    {
        private readonly HttpListener _listener = new();
        private CancellationTokenSource? _cts;

        private readonly object _gate = new();
        private readonly Dictionary<string, WebSocket> _hostsByRid = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<System.Text.Json.JsonElement>> _helloByRid =
            new(StringComparer.Ordinal);
        private readonly TaskCompletionSource<bool> _connected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Uri Uri { get; }
        public Action<byte[]>? OnHostFrame { get; set; }

        /// <summary>The rid the test peers a client up against (the runtime rid).</summary>
        public string RuntimeRid { get; set; } = "";

        public FakeRelay()
        {
            var port = GetFreePort();
            Uri = new Uri($"ws://127.0.0.1:{port}/relay");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/relay/");
        }

        public Task StartAsync()
        {
            _cts = new CancellationTokenSource();
            _listener.Start();
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            return Task.CompletedTask;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch { return; }

                if (!ctx.Request.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                    continue;
                }
                var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
                _connected.TrySetResult(true);
                _ = Task.Run(() => HostReadLoopAsync(wsCtx.WebSocket, ct));
            }
        }

        private async Task HostReadLoopAsync(WebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[8192];
            using var message = new System.IO.MemoryStream();
            string? rid = null;
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                try
                {
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                            return;
                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);
                }
                catch
                {
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(message.ToArray());
                    rid = doc.RootElement.GetProperty("rid").GetString();
                    if (!string.IsNullOrEmpty(rid))
                        RegisterHost(rid, socket, doc.RootElement.Clone());
                }
                else if (result.MessageType == WebSocketMessageType.Binary
                    && string.Equals(rid, RuntimeRid, StringComparison.Ordinal))
                {
                    OnHostFrame?.Invoke(message.ToArray());
                }
            }
        }

        private void RegisterHost(string rid, WebSocket socket, System.Text.Json.JsonElement hello)
        {
            lock (_gate)
            {
                _hostsByRid[rid] = socket;
                HelloWaiter(rid).TrySetResult(hello);
            }
        }

        private TaskCompletionSource<System.Text.Json.JsonElement> HelloWaiter(string rid)
        {
            if (!_helloByRid.TryGetValue(rid, out var tcs))
            {
                tcs = new TaskCompletionSource<System.Text.Json.JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                _helloByRid[rid] = tcs;
            }
            return tcs;
        }

        public Task<System.Text.Json.JsonElement> WaitForHelloAsync()
            => WaitForHelloAsync(TimeSpan.FromSeconds(5));

        public Task<System.Text.Json.JsonElement> WaitForHelloAsync(TimeSpan timeout)
        {
            Task<System.Text.Json.JsonElement> task;
            lock (_gate)
                task = HelloWaiter(RuntimeRid).Task;
            return task.WaitAsync(timeout);
        }

        public async Task<bool> WaitForConnectionAsync(TimeSpan timeout)
        {
            try { return await _connected.Task.WaitAsync(timeout); }
            catch (TimeoutException) { return false; }
        }

        public async Task SendPeerUpAsync(byte[] connSalt)
        {
            var saltB64 = RelayCrypto.Base64UrlNoPad(connSalt);
            var json = Encoding.UTF8.GetBytes($"{{\"e\":\"peer-up\",\"salt\":\"{saltB64}\"}}");
            await HostFor(RuntimeRid).SendAsync(json, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task ForwardToHostAsync(byte[] frame)
        {
            await HostFor(RuntimeRid).SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        private WebSocket HostFor(string rid)
        {
            lock (_gate)
            {
                if (!_hostsByRid.TryGetValue(rid, out var socket))
                    throw new InvalidOperationException($"no host registered for rid {rid}");
                return socket;
            }
        }

        private static int GetFreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            lock (_gate)
            {
                foreach (var s in _hostsByRid.Values)
                {
                    try { s.Abort(); } catch { }
                    try { s.Dispose(); } catch { }
                }
            }
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}
