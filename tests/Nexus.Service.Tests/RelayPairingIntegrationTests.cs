using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Relay;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

/// <summary>
/// End-to-end integration for claim-over-relay (Phase 1: a brand-new phone with
/// no LAN reachability pairs through the cloud relay). Stands up a multi-rid
/// loopback fake relay and proves:
///   • with relay + remote on, the PC registers a host socket on rid_pair for
///     the outstanding QR pair token,
///   • a phone that peers up on rid_pair and sends a sealed claim request gets a
///     sealed claim-ok carrying a usable session token,
///   • the pair token is single-use (a second claim attempt fails),
///   • once the session exists, the PC bridges its RUNTIME rid into the hub
///     (a relayed `sub` command reaches the hub on the new session's rid).
/// </summary>
public sealed class RelayPairingIntegrationTests
{
    private const string DeviceName = "Nicola's iPhone";
    private const string Topic = "relay-pair-roundtrip-topic";

    [Fact]
    public async Task ClaimOverRelay_MintsUsableSession_TokenSingleUse_AndRuntimeRidBridges()
    {
        using var relay = new MultiRidFakeRelay();
        await relay.StartAsync();

        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.RemoteControlEnabled = true;
            s.Auth.RelayEnabled = true;
            s.Auth.PanelPhoneSessions = new List<PanelPhoneSessionToken>();
        });

        var hub = new MultiplexHub();
        var pairing = new Nexus.Service.Panel.PanelPhonePairingService(store, hub) { PublicLinkHost = "" };

        using var service = new RelayConnectionService(
            NullLogger<RelayConnectionService>.Instance, pairing, store, hub)
        {
            Endpoint = relay.Uri,
        };
        await service.StartAsync(CancellationToken.None);

        // 1) Mint a QR pair token (this is the real CreatePairQr mint path; it
        //    fires OutstandingPairTokensChanged → the relay desires a pair link).
        var qr = pairing.CreatePairQr();
        var pairToken = ExtractPairToken(qr.Url);
        Assert.False(string.IsNullOrEmpty(pairToken));

        var pairRoot = RelayCrypto.DerivePairRoot(pairToken);
        var ridPair = RelayCrypto.DeriveRid(pairRoot);

        // The PC registers a host socket on rid_pair.
        await relay.WaitForHostAsync(ridPair, TimeSpan.FromSeconds(5));

        // 2) Phone peers up on rid_pair with a connSalt, derives the claim key,
        //    and sends the sealed claim request (dir=2, client→host).
        var connSalt = new byte[RelayCrypto.ConnSaltLength];
        for (var i = 0; i < connSalt.Length; i++) connSalt[i] = (byte)(i + 0x30);
        var claimKey = RelayCrypto.DeriveAeadKey(pairRoot, connSalt);
        await relay.SendPeerUpAsync(ridPair, connSalt);

        var claimReq = new RelayClaimRequest { Type = RelayClaimMessageTypes.Claim, DeviceName = DeviceName };
        var reqBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            claimReq, Nexus.Service.Serialization.AppJsonContext.Default.RelayClaimRequest);
        var sealedReq = RelayCrypto.Seal(claimKey, RelayCrypto.DirClientToHost, counter: 0, reqBytes);
        await relay.ForwardToHostAsync(ridPair, sealedReq);

        // 3) The PC replies with ONE sealed claim-ok frame (dir=1, host→client).
        var replyFrame = await relay.WaitForHostFrameAsync(ridPair, TimeSpan.FromSeconds(5));
        var (dir, counter, replyPlain) = RelayCrypto.Open(claimKey, replyFrame);
        Assert.Equal(RelayCrypto.DirHostToClient, dir);
        Assert.Equal(0ul, counter);

        var reply = System.Text.Json.JsonSerializer.Deserialize(
            replyPlain, Nexus.Service.Serialization.AppJsonContext.Default.RelayClaimResponse);
        Assert.NotNull(reply);
        Assert.Equal(RelayClaimMessageTypes.ClaimOk, reply!.Type);
        Assert.False(string.IsNullOrEmpty(reply.SessionToken));
        Assert.False(string.IsNullOrEmpty(reply.MachineName)); // OS / override machine name

        // 4) The minted session token must be a usable session credential.
        Assert.True(pairing.ValidateSessionToken(reply.SessionToken),
            "claim-ok session token did not validate as an active session");

        // The stored session shows up as relayable and carries the phone's name.
        var active = pairing.GetActiveRelaySessions();
        Assert.Single(active);

        // 5) Single-use: claiming the same pair token again fails (it is consumed).
        var second = pairing.ClaimCore(
            pairToken, deviceName: "second", userAgent: "", remoteAddress: "",
            overRelay: true, claimedOverHttps: false);
        Assert.False(second.Ok);
        Assert.Equal("pairing token expired", second.Error);

        // 6) The new session's RUNTIME rid now bridges into the hub. Derive its
        //    rid from the session token (exactly what the phone would do), wait
        //    for the PC's runtime host socket, peer up, and round-trip a `sub`.
        var sessionRoot = RelayCrypto.DeriveRelayRoot(reply.SessionToken);
        var sessionRid = RelayCrypto.DeriveRid(sessionRoot);
        Assert.NotEqual(ridPair, sessionRid);

        await relay.WaitForHostAsync(sessionRid, TimeSpan.FromSeconds(5));

        var runtimeSalt = new byte[RelayCrypto.ConnSaltLength];
        for (var i = 0; i < runtimeSalt.Length; i++) runtimeSalt[i] = (byte)(i + 0x50);
        var runtimeKey = RelayCrypto.DeriveAeadKey(sessionRoot, runtimeSalt);
        await relay.SendPeerUpAsync(sessionRid, runtimeSalt);

        var subCommand = Encoding.UTF8.GetBytes($"{{\"sub\":[\"{Topic}\"]}}");
        var subFrame = RelayCrypto.Seal(runtimeKey, RelayCrypto.DirClientToHost, counter: 0, subCommand);
        await relay.ForwardToHostAsync(sessionRid, subFrame);

        await WaitUntilAsync(() => hub.TopicHasSubscribers(Topic), TimeSpan.FromSeconds(5),
            "relayed `sub` on the new session rid never reached the hub");

        await service.StopAsync(CancellationToken.None);
    }

    private static string ExtractPairToken(string url)
    {
        // url is .../panel/phone?pair=<token>&machineName=...  (PublicLinkHost="").
        var query = new Uri(url).Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            if (pair.AsSpan(0, eq).SequenceEqual("pair"))
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return "";
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
    /// Loopback fake relay that accepts MANY host connections and routes by rid
    /// (the host hello's <c>rid</c>). Acts as the relay AND the simulated client:
    /// the test peers a client up against a given rid, forwards sealed client
    /// frames to that rid's host socket, and captures host→client frames per rid.
    /// </summary>
    private sealed class MultiRidFakeRelay : IDisposable
    {
        private readonly HttpListener _listener = new();
        private CancellationTokenSource? _cts;

        private readonly object _gate = new();
        private readonly Dictionary<string, WebSocket> _hostsByRid = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<bool>> _hostWaiters = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ConcurrentQueue<byte[]>> _hostFrames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<byte[]>> _frameWaiters = new(StringComparer.Ordinal);

        public Uri Uri { get; }

        public MultiRidFakeRelay()
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
                        RegisterHost(rid, socket);
                }
                else if (result.MessageType == WebSocketMessageType.Binary && rid is not null)
                {
                    DeliverHostFrame(rid, message.ToArray());
                }
            }
        }

        private void RegisterHost(string rid, WebSocket socket)
        {
            lock (_gate)
            {
                _hostsByRid[rid] = socket;
                if (_hostWaiters.TryGetValue(rid, out var w))
                    w.TrySetResult(true);
            }
        }

        private void DeliverHostFrame(string rid, byte[] frame)
        {
            lock (_gate)
            {
                if (_frameWaiters.TryGetValue(rid, out var w) && w.TrySetResult(frame))
                {
                    _frameWaiters.Remove(rid);
                    return;
                }
                if (!_hostFrames.TryGetValue(rid, out var q))
                {
                    q = new ConcurrentQueue<byte[]>();
                    _hostFrames[rid] = q;
                }
                q.Enqueue(frame);
            }
        }

        public Task WaitForHostAsync(string rid, TimeSpan timeout)
        {
            TaskCompletionSource<bool> tcs;
            lock (_gate)
            {
                if (_hostsByRid.ContainsKey(rid))
                    return Task.CompletedTask;
                if (!_hostWaiters.TryGetValue(rid, out tcs!))
                {
                    tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _hostWaiters[rid] = tcs;
                }
            }
            return tcs.Task.WaitAsync(timeout);
        }

        public Task<byte[]> WaitForHostFrameAsync(string rid, TimeSpan timeout)
        {
            TaskCompletionSource<byte[]> tcs;
            lock (_gate)
            {
                if (_hostFrames.TryGetValue(rid, out var q) && q.TryDequeue(out var frame))
                    return Task.FromResult(frame);
                tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                _frameWaiters[rid] = tcs;
            }
            return tcs.Task.WaitAsync(timeout);
        }

        public async Task SendPeerUpAsync(string rid, byte[] connSalt)
        {
            var socket = HostFor(rid);
            var saltB64 = RelayCrypto.Base64UrlNoPad(connSalt);
            var json = Encoding.UTF8.GetBytes($"{{\"e\":\"peer-up\",\"salt\":\"{saltB64}\"}}");
            await socket.SendAsync(json, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task ForwardToHostAsync(string rid, byte[] frame)
        {
            var socket = HostFor(rid);
            await socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
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
