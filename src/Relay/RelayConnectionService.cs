using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Relay;

/// <summary>
/// Holds the PC's outbound cloud-relay sockets. When the user has enabled BOTH
/// remote control AND the relay opt-in, the service opens ONE host
/// <see cref="ClientWebSocket"/> per paired phone session to the relay
/// (<see cref="RelayUrl"/>), registers as that session's <c>host</c> via the
/// rid derived from the session's relay root, and bridges relayed frames into
/// the shared <see cref="MultiplexHub"/> tagged with the session id — so the
/// Pair Remote killswitch (KickAll / KickPhoneSessions) closes a relayed
/// session exactly like a LAN one.
///
/// Entirely event-driven: it reconciles its set of host sockets to the desired
/// state on start and on every <see cref="IConfigStore.OnChanged"/> (relay
/// on/off, remote-control on/off, session add/remove all flow through that one
/// signal). There is no poll loop. A dropped relay socket reconnects with
/// capped exponential backoff; waits are on cancellation tokens / channel
/// reads, never a sleep-to-win-a-race.
/// </summary>
public sealed class RelayConnectionService : BackgroundService
{
    /// <summary>The production relay gateway. Configless; the rid is the only routing key.</summary>
    public const string RelayUrl = "wss://api.hellonexus.com/relay";

    private const int ReceiveBufferSize = 8192;
    private const int InitialReconnectDelayMs = 1_000;
    private const int MaxReconnectDelayMs = 30_000;
    // Relay caps inbound frames at 256 KB; refuse to buffer anything larger so a
    // hostile relay can't push us into unbounded allocation.
    private const int MaxFrameBytes = 256 * 1024;

    private const string EventKey = "e";
    private const string SaltKey = "salt";
    private const string PeerUp = "peer-up";
    private const string PeerDown = "peer-down";

    private readonly ILogger<RelayConnectionService> _log;
    private readonly PanelPhonePairingService _pairing;
    private readonly IConfigStore _store;
    private readonly MultiplexHub _hub;

    private readonly object _gate = new();
    private readonly Dictionary<string, HostLink> _links = new(StringComparer.Ordinal);
    private CancellationToken _serviceCt = CancellationToken.None;
    private bool _started;

    /// <summary>
    /// Relay endpoint. Defaults to <see cref="RelayUrl"/>; tests point it at a
    /// local fake relay. Settable only before the service starts.
    /// </summary>
    public Uri Endpoint { get; set; } = new Uri(RelayUrl);

    /// <summary>
    /// Opens the underlying transport to the relay. Default creates a real
    /// <see cref="ClientWebSocket"/>; tests swap in an in-process transport.
    /// </summary>
    public Func<Uri, CancellationToken, Task<WebSocket>> TransportFactory { get; set; } = DefaultTransportFactory;

    public RelayConnectionService(
        ILogger<RelayConnectionService> log,
        PanelPhonePairingService pairing,
        IConfigStore store,
        MultiplexHub hub)
    {
        _log = log;
        _pairing = pairing;
        _store = store;
        _hub = hub;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _serviceCt = stoppingToken;
        _started = true;
        _store.OnChanged += OnSettingsChanged;
        Reconcile();

        // Tear everything down when the host stops.
        stoppingToken.Register(() =>
        {
            _store.OnChanged -= OnSettingsChanged;
            CloseAllLinks();
        });
        return Task.CompletedTask;
    }

    private static Task<WebSocket> DefaultTransportFactory(Uri uri, CancellationToken ct)
        => ConnectClientWebSocketAsync(uri, ct);

    private static async Task<WebSocket> ConnectClientWebSocketAsync(Uri uri, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(uri, ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private void OnSettingsChanged()
    {
        // IConfigStore.OnChanged fires on the persistence thread; hop off so we
        // never block a writer while opening / closing sockets.
        _ = Task.Run(Reconcile);
    }

    /// <summary>
    /// Bring the live set of host links into agreement with the desired set:
    /// one link per active relay session when (remote-control AND relay) are on,
    /// none otherwise. Adds links for new sessions, stops links for sessions
    /// that vanished or when the feature was turned off.
    /// </summary>
    private void Reconcile()
    {
        if (!_started)
            return;

        var enabled = _pairing.GetRemoteControlEnabled() && _pairing.GetRelayEnabled();

        // Desired set: one host link per active relay session when enabled,
        // none otherwise (so turning either switch off tears every link down).
        var desired = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (enabled)
        {
            foreach (var s in _pairing.GetActiveRelaySessions())
                desired[s.Id] = s.RelayRoot;
        }

        List<HostLink> toStop = new();
        List<HostLink> toStart = new();
        lock (_gate)
        {
            if (_serviceCt.IsCancellationRequested)
                return;

            // Stop links no longer desired.
            foreach (var (id, link) in _links)
            {
                if (!desired.ContainsKey(id))
                    toStop.Add(link);
            }
            foreach (var link in toStop)
                _links.Remove(link.SessionId);

            // Start links newly desired.
            foreach (var (id, relayRoot) in desired)
            {
                if (_links.ContainsKey(id))
                    continue;
                var link = new HostLink(this, id, relayRoot);
                _links[id] = link;
                toStart.Add(link);
            }
        }

        foreach (var link in toStop)
            link.Stop();
        foreach (var link in toStart)
            link.Start(_serviceCt);
    }

    private void CloseAllLinks()
    {
        List<HostLink> links;
        lock (_gate)
        {
            links = new List<HostLink>(_links.Values);
            _links.Clear();
        }
        foreach (var link in links)
            link.Stop();
    }

    /// <summary>
    /// One host leg: owns the reconnect loop, the relay socket, and the current
    /// relayed hub session (if a client is presently peered up).
    /// </summary>
    private sealed class HostLink
    {
        private readonly RelayConnectionService _owner;
        private readonly byte[] _relayRoot;
        private readonly string _rid;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        public string SessionId { get; }

        public HostLink(RelayConnectionService owner, string sessionId, byte[] relayRoot)
        {
            _owner = owner;
            SessionId = sessionId;
            _relayRoot = relayRoot;
            _rid = RelayCrypto.DeriveRid(relayRoot);
        }

        public void Start(CancellationToken serviceCt)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(serviceCt);
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { /* ignored */ }
            // The loop observes cancellation and disposes the socket; we don't
            // await here so a single stuck socket can't stall reconciliation.
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var delayMs = InitialReconnectDelayMs;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(ct).ConfigureAwait(false);
                    // Clean return (host socket closed by us / the relay): if we
                    // weren't cancelled, reconnect from the base backoff.
                    delayMs = InitialReconnectDelayMs;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _owner._log.LogDebug(ex, "relay host link for session {SessionId} dropped; reconnecting", SessionId);
                }

                if (ct.IsCancellationRequested)
                    break;

                try { await Task.Delay(delayMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                delayMs = Math.Min(delayMs * 2, MaxReconnectDelayMs);
            }
        }

        private async Task RunOnceAsync(CancellationToken ct)
        {
            WebSocket transport = await _owner.TransportFactory(_owner.Endpoint, ct).ConfigureAwait(false);
            await using var _ = new WebSocketDisposer(transport);

            // 1) Host hello.
            await SendHelloAsync(transport, ct).ConfigureAwait(false);

            // 2) Receive loop: peer-up / peer-down (TEXT) + forwarded frames (BINARY).
            RelayWebSocket? session = null;
            Task? sessionTask = null;
            var buffer = new byte[ReceiveBufferSize];
            using var message = new System.IO.MemoryStream();

            try
            {
                while (!ct.IsCancellationRequested && transport.State == WebSocketState.Open)
                {
                    message.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await transport.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                            return; // relay closed the host socket → outer loop reconnects
                        if (message.Length + result.Count > MaxFrameBytes)
                            throw new InvalidOperationException("relay frame exceeds 256 KB cap");
                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        (session, sessionTask) = HandleControl(message.ToArray(), session, sessionTask, transport, ct);
                    }
                    else if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        // Forwarded encrypted client frame: hand to the current
                        // relayed session for decrypt + delivery to the hub.
                        session?.EnqueueInbound(message.ToArray());
                    }
                }
            }
            finally
            {
                // End any in-flight relayed session so the hub's receive loop exits.
                session?.CompleteInbound();
                if (sessionTask is not null)
                {
                    try { await sessionTask.ConfigureAwait(false); } catch { /* hub loop teardown */ }
                }
            }
        }

        private (RelayWebSocket?, Task?) HandleControl(
            byte[] textBytes, RelayWebSocket? session, Task? sessionTask, WebSocket transport, CancellationToken ct)
        {
            string? evt;
            string? saltB64;
            try
            {
                using var doc = JsonDocument.Parse(textBytes);
                var root = doc.RootElement;
                evt = root.TryGetProperty(EventKey, out var e) ? e.GetString() : null;
                saltB64 = root.TryGetProperty(SaltKey, out var s) ? s.GetString() : null;
            }
            catch
            {
                // Malformed control frame: ignore (TEXT-after-hello is otherwise unused).
                return (session, sessionTask);
            }

            if (string.Equals(evt, PeerUp, StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(saltB64))
                    return (session, sessionTask);

                // A fresh client peered up. End any prior session first
                // (defensive: relay should have sent peer-down).
                session?.CompleteInbound();

                byte[] connSalt;
                try { connSalt = RelayCrypto.FromBase64UrlNoPad(saltB64); }
                catch { return (session, sessionTask); }
                if (connSalt.Length != RelayCrypto.ConnSaltLength)
                    return (session, sessionTask);

                var aeadKey = RelayCrypto.DeriveAeadKey(_relayRoot, connSalt);
                // Host endpoint: send dir=1 (host→client), expect dir=2 (client→host).
                var relayWs = new RelayWebSocket(
                    transport, aeadKey, RelayCrypto.DirHostToClient, RelayCrypto.DirClientToHost);

                // Drive the hub on this relayed socket, tagged with the phone
                // session id so the killswitch can close it.
                var task = _owner._hub.HandleClientAsync(relayWs, SessionId, ct);
                return (relayWs, task);
            }

            if (string.Equals(evt, PeerDown, StringComparison.Ordinal))
            {
                // Client went away: end the relayed hub session but KEEP the host
                // socket open so the next client peers up on the same rid.
                session?.CompleteInbound();
                return (null, sessionTask);
            }

            return (session, sessionTask);
        }

        private async Task SendHelloAsync(WebSocket transport, CancellationToken ct)
        {
            var hello = new RelayHostHello { V = 1, Role = "host", Rid = _rid };
            var json = JsonSerializer.SerializeToUtf8Bytes(hello, AppJsonContext.Default.RelayHostHello);
            await transport
                .SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Disposes a WebSocket once the relay loop using it returns / faults.</summary>
    private readonly struct WebSocketDisposer : IAsyncDisposable
    {
        private readonly WebSocket _socket;
        public WebSocketDisposer(WebSocket socket) => _socket = socket;

        public ValueTask DisposeAsync()
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    // Best-effort orderly close; don't block teardown on it.
                    _ = _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "host-link-end", CancellationToken.None);
                }
            }
            catch { /* ignored */ }
            try { _socket.Dispose(); } catch { /* ignored */ }
            return ValueTask.CompletedTask;
        }
    }
}
