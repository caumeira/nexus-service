using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Qos.Service.Sockets;

/// <summary>
/// Single multiplexed WebSocket hub. Clients connect to one endpoint and send
/// subscribe/unsubscribe commands to choose which topics they receive.
///
/// Protocol:
///   Client → Server:  {"sub":["processes","network"]}
///   Client → Server:  {"unsub":["processes"]}
///   Server → Client:  {"t":"processes","d":{…}}
///
/// Broadcasters call <see cref="BroadcastTopicAsync"/> with pre-built envelope
/// bytes; the hub fans out only to clients subscribed to that topic.
/// </summary>
public sealed class MultiplexHub
{
    private readonly ConcurrentDictionary<Guid, SubscribedClient> _clients = new();
    private readonly ConcurrentDictionary<string, Func<ReadOnlyMemory<byte>?>> _snapshotProviders =
        new(StringComparer.OrdinalIgnoreCase);

    public delegate void TopicEvent(string topic);
    public event TopicEvent? OnTopicFirstSubscriber;
    public event TopicEvent? OnTopicLastUnsubscriber;

    /// <summary>
    /// Register a snapshot provider for a topic whose broadcast cadence is slow
    /// or event-driven. When a client subscribes, the hub immediately sends the
    /// provider's current envelope (if any) to that one client, so late
    /// subscribers do not wait for the next periodic or change-driven tick.
    /// The provider should return a pre-built envelope (use <see cref="WsEnvelope.Build"/>)
    /// or <c>null</c> when no state has been produced yet.
    ///
    /// Ordering: the snapshot send is fire-and-forget, so a fresh broadcast
    /// fired between subscribe and send-completion can arrive at the client
    /// before the snapshot. Only register snapshot providers for topics whose
    /// payloads are idempotent / order-tolerant (latest-wins). Do not use this
    /// pattern for topics carrying sequence numbers or deltas.
    /// </summary>
    public void RegisterSnapshotProvider(string topic, Func<ReadOnlyMemory<byte>?> provider)
    {
        _snapshotProviders[topic] = provider;
    }

    public void UnregisterSnapshotProvider(string topic)
    {
        _snapshotProviders.TryRemove(topic, out _);
    }

    internal bool TryGetTopicSnapshot(string topic, out ReadOnlyMemory<byte> envelope)
    {
        if (_snapshotProviders.TryGetValue(topic, out var provider))
        {
            ReadOnlyMemory<byte>? built;
            try
            { built = provider(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[multiplex-hub] snapshot provider for '{topic}' threw: {ex.Message}");
                built = null;
            }
            if (built is { } e)
            {
                envelope = e;
                return true;
            }
        }
        envelope = default;
        return false;
    }

    public bool TopicHasSubscribers(string topic)
    {
        foreach (var (_, client) in _clients)
        {
            if (client.IsSubscribed(topic))
                return true;
        }
        lock (_testSubsLock)
        {
            return _testSubs.TryGetValue(topic, out var n) && n > 0;
        }
    }

    public int TopicSubscriberCount(string topic)
    {
        int count = 0;
        foreach (var (_, client) in _clients)
        {
            if (client.IsSubscribed(topic))
                count++;
        }
        lock (_testSubsLock)
        {
            if (_testSubs.TryGetValue(topic, out var n))
            {
                count += n;
            }
        }
        return count;
    }

    public int ClientCount => _clients.Count;

    public Task HandleClientAsync(WebSocket socket, CancellationToken cancellationToken = default)
        => HandleClientAsync(socket, phoneSessionId: null, cancellationToken);

    /// <summary>
    /// Accept a multiplexed WebSocket. When <paramref name="phoneSessionId"/>
    /// is non-null, this client is treated as a Pair Remote session and can
    /// be force-closed by <see cref="KickPhoneSessionsAsync"/> /
    /// <see cref="KickAllPhoneAsync"/>. Local desktop / panel-kiosk callers
    /// pass null and stay unkickable.
    /// </summary>
    public async Task HandleClientAsync(WebSocket socket, string? phoneSessionId, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var client = new SubscribedClient(socket, phoneSessionId);
        _clients[id] = client;

        try
        {
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                }
                catch (WebSocketException) { break; }

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    if (msg == "ping")
                        continue;
                    ProcessCommand(id, client, msg);
                }
            }
        }
        finally
        {
            _clients.TryRemove(id, out _);
            var topics = client.GetSubscriptions();
            client.Dispose();

            try
            {
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }

            foreach (var topic in topics)
            {
                if (!TopicHasSubscribers(topic))
                {
                    try
                    { OnTopicLastUnsubscriber?.Invoke(topic); }
                    catch { }
                }
            }
        }
    }

    public async Task BroadcastTopicAsync(string topic, ReadOnlyMemory<byte> envelope)
    {
        foreach (var (_, client) in _clients)
        {
            if (client.IsSubscribed(topic))
            {
                await client.SendAsync(envelope, WebSocketMessageType.Text);
            }
        }
        // Fire the test-only observer if one is attached. Integration tests use
        // this to verify broadcasters actually hit the wire without needing a
        // real WebSocket client. Production code doesn't attach a handler so
        // this event is a no-op at runtime.
        try
        { OnBroadcastForTest?.Invoke(topic, envelope); }
        catch { /* swallow */ }
    }

    /// <summary>
    /// Force-close every connected phone-session WebSocket whose session id
    /// matches one in <paramref name="sessionIds"/>. The socket transitions
    /// to <see cref="WebSocketState.CloseSent"/> which unblocks the receive
    /// loop in <see cref="HandleClientAsync"/>, and the finalizer there
    /// removes the entry from <see cref="_clients"/>. Local desktop / panel
    /// clients (phoneSessionId == null) are never touched. Kicks fan out
    /// in parallel so a single slow socket can't delay the rest - "OFF
    /// means OFF" must not stall on one stuck client.
    /// </summary>
    public Task KickPhoneSessionsAsync(IReadOnlyCollection<string> sessionIds)
    {
        if (sessionIds.Count == 0)
            return Task.CompletedTask;

        var ids = new HashSet<string>(sessionIds, StringComparer.Ordinal);
        var tasks = new List<Task>();
        foreach (var (_, client) in _clients)
        {
            if (client.PhoneSessionId is { } sid && ids.Contains(sid))
                tasks.Add(client.CloseRevokedAsync());
        }
        return tasks.Count == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
    }

    /// <summary>
    /// Force-close every connected phone-session WebSocket. Used when the
    /// remote-control killswitch is toggled off and on "Remove all sessions".
    /// Parallel for the same reason as <see cref="KickPhoneSessionsAsync"/>.
    /// </summary>
    public Task KickAllPhoneAsync()
    {
        var tasks = new List<Task>();
        foreach (var (_, client) in _clients)
        {
            if (client.PhoneSessionId is not null)
                tasks.Add(client.CloseRevokedAsync());
        }
        return tasks.Count == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
    }

    /// <summary>
    /// Test-only hook. Fires after every <see cref="BroadcastTopicAsync"/> call,
    /// including topics with zero subscribers. Integration tests subscribe to this
    /// instead of wiring a real WebSocket client.
    /// </summary>
    internal event Action<string, ReadOnlyMemory<byte>>? OnBroadcastForTest;

    /// <summary>
    /// Test-only: register a phantom subscriber for <paramref name="topic"/> so
    /// <see cref="TopicHasSubscribers"/> returns true and the broadcaster runs
    /// its full gather path. Returns a disposable that removes the subscription.
    /// </summary>
    internal IDisposable AddTestSubscription(string topic)
    {
        lock (_testSubsLock)
        {
            _testSubs.TryGetValue(topic, out var count);
            _testSubs[topic] = count + 1;
            if (count == 0)
            {
                try
                { OnTopicFirstSubscriber?.Invoke(topic); }
                catch { }
            }
        }
        return new TestSubscriptionHandle(this, topic);
    }

    private readonly Dictionary<string, int> _testSubs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _testSubsLock = new();

    private void ReleaseTestSubscription(string topic)
    {
        lock (_testSubsLock)
        {
            if (!_testSubs.TryGetValue(topic, out var count) || count <= 0)
            {
                return;
            }
            if (count == 1)
            {
                _testSubs.Remove(topic);
                try
                { OnTopicLastUnsubscriber?.Invoke(topic); }
                catch { }
            }
            else
            {
                _testSubs[topic] = count - 1;
            }
        }
    }

    private sealed class TestSubscriptionHandle : IDisposable
    {
        private readonly MultiplexHub _hub;
        private readonly string _topic;
        private bool _disposed;
        public TestSubscriptionHandle(MultiplexHub hub, string topic) { _hub = hub; _topic = topic; }
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _hub.ReleaseTestSubscription(_topic);
        }
    }

    private void ProcessCommand(Guid clientId, SubscribedClient client, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("sub", out var subArr) && subArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in subArr.EnumerateArray())
                {
                    var topic = item.GetString();
                    if (topic is null)
                        continue;

                    bool wasFirst = !TopicHasSubscribers(topic);
                    client.Subscribe(topic);
                    if (wasFirst)
                    {
                        try
                        { OnTopicFirstSubscriber?.Invoke(topic); }
                        catch { }
                    }

                    // Snapshot-on-subscribe: deliver the latest known envelope
                    // to this single client so late subscribers don't wait for
                    // the next slow / event-driven broadcast tick.
                    if (TryGetTopicSnapshot(topic, out var snapshot))
                    {
                        _ = client.SendAsync(snapshot, WebSocketMessageType.Text);
                    }
                }
            }

            if (root.TryGetProperty("unsub", out var unsubArr) && unsubArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in unsubArr.EnumerateArray())
                {
                    var topic = item.GetString();
                    if (topic is null)
                        continue;

                    client.Unsubscribe(topic);
                    if (!TopicHasSubscribers(topic))
                    {
                        try
                        { OnTopicLastUnsubscriber?.Invoke(topic); }
                        catch { }
                    }
                }
            }
        }
        catch
        {
            // Malformed command — ignore.
        }
    }

    private sealed class SubscribedClient : IDisposable
    {
        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly HashSet<string> _topics = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _topicLock = new();

        /// <summary>
        /// When non-null, this client is a Pair Remote session and can be
        /// force-disconnected by <see cref="MultiplexHub.KickPhoneSessionsAsync"/>.
        /// Null means a trusted local client (desktop app or panel kiosk).
        /// </summary>
        public string? PhoneSessionId { get; }

        public SubscribedClient(WebSocket socket, string? phoneSessionId = null)
        {
            _socket = socket;
            PhoneSessionId = phoneSessionId;
        }

        public async Task CloseRevokedAsync()
        {
            await _writeLock.WaitAsync();
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "revoked", CancellationToken.None);
                }
            }
            catch { }
            finally
            {
                _writeLock.Release();
            }
        }

        public bool IsSubscribed(string topic)
        {
            lock (_topicLock)
                return _topics.Contains(topic);
        }

        public void Subscribe(string topic)
        {
            lock (_topicLock)
                _topics.Add(topic);
        }

        public void Unsubscribe(string topic)
        {
            lock (_topicLock)
                _topics.Remove(topic);
        }

        public string[] GetSubscriptions()
        {
            lock (_topicLock)
                return _topics.ToArray();
        }

        public async Task SendAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType type)
        {
            if (_socket.State != WebSocketState.Open)
                return;

            await _writeLock.WaitAsync();
            try
            {
                await _socket.SendAsync(payload, type, endOfMessage: true, CancellationToken.None);
            }
            catch { }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose() => _writeLock.Dispose();
    }
}
