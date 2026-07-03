using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;

namespace Nexus.Service.Rtc;

/// <summary>
/// Adapts one open <see cref="RTCDataChannel"/> ("runtime") as the
/// <c>WebSocket transport</c> parameter <c>RelayWebSocket</c> expects, so the
/// same sealed-frame codec that drives a cloud-relay connection drives a direct
/// P2P data channel unchanged.
///
/// Only the send direction flows through this adapter
/// (<see cref="SendAsync"/>): inbound frames arrive via the channel's
/// <c>onmessage</c> callback, which the owning session feeds straight into
/// <c>RelayWebSocket.EnqueueInbound</c>, bypassing <see cref="ReceiveAsync"/>
/// entirely. <see cref="ReceiveAsync"/> exists only to satisfy the abstract
/// <see cref="WebSocket"/> contract.
/// </summary>
public sealed class RtcDataChannelTransport : WebSocket
{
    /// <summary>Matches RTCSctpTransport.SCTP_DEFAULT_MAX_MESSAGE_SIZE and the relay's frame cap.</summary>
    private const int MaxFrameBytes = 256 * 1024;

    private readonly RTCDataChannel _channel;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private WebSocketState _state = WebSocketState.Open;
    private WebSocketCloseStatus? _closeStatus;
    private string? _closeStatusDescription;

    /// <summary>
    /// Fires once when the channel is torn down, whether from a local
    /// <see cref="Abort"/> (a hub kick calls this through RelayWebSocket) or the
    /// underlying data channel closing on its own. The owning session disposes
    /// its whole peer connection on this signal.
    /// </summary>
    public event Action? OnAborted;

    public RtcDataChannelTransport(RTCDataChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _channel.onclose += HandleChannelClosed;
    }

    public override WebSocketState State => _state;
    public override WebSocketCloseStatus? CloseStatus => _closeStatus;
    public override string? CloseStatusDescription => _closeStatusDescription;
    public override string? SubProtocol => null;

    public override async Task SendAsync(
        ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        if (messageType != WebSocketMessageType.Binary)
            return;
        if (_state != WebSocketState.Open)
            return;
        if (buffer.Count > MaxFrameBytes)
            throw new InvalidOperationException("rtc data channel frame exceeds 256 KB cap");

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != WebSocketState.Open)
                return;
            var data = new byte[buffer.Count];
            Buffer.BlockCopy(buffer.Array!, buffer.Offset, data, 0, buffer.Count);
            _channel.send(data);
        }
        catch (Exception) when (_state != WebSocketState.Open)
        {
            // Racing teardown - swallow, matching RelayWebSocket.SendAsync.
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        => throw new NotSupportedException("inbound rtc frames flow through RelayWebSocket.EnqueueInbound, not this adapter");

    public override Task CloseAsync(
        WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _closeStatus = closeStatus;
        _closeStatusDescription = statusDescription;
        Abort();
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override void Abort()
    {
        if (_state is WebSocketState.Aborted or WebSocketState.Closed)
            return;
        _closeStatus ??= WebSocketCloseStatus.NormalClosure;
        _state = WebSocketState.Aborted;
        _channel.onclose -= HandleChannelClosed;
        try { _channel.close(); } catch { /* best effort */ }
        OnAborted?.Invoke();
    }

    public override void Dispose()
    {
        if (_state == WebSocketState.Open)
            _state = WebSocketState.Closed;
        _sendLock.Dispose();
    }

    private void HandleChannelClosed() => Abort();
}
