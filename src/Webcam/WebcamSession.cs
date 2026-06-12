using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Webcam;

public enum WebcamFrameError
{
    None = 0,
    TruncatedHeader,
    UnknownVersion,
    UnknownCodec,
    Oversize,
}

/// <summary>Receives validated stream frames; implemented by <see cref="WebcamSessionManager"/>.</summary>
public interface IWebcamFrameSink
{
    /// <summary>False rejects the frame as a protocol violation (codec drift
    /// from the armed format); the session closes the stream in response.</summary>
    ValueTask<bool> OnFrameAsync(ReadOnlyMemory<byte> payload, WebcamFrameInfo info, CancellationToken cancellationToken);
}

/// <summary>
/// Per-connection stream state machine. Each WS binary message is one encoded
/// video frame: a fixed little-endian header followed by the codec payload.
/// The session assembles fragmented messages into a pooled buffer, parses and
/// validates the header (span slicing, no allocation per frame), and forwards
/// the payload slice to the sink. Protocol violations close the socket; the
/// caller treats any return as a disconnect.
/// </summary>
public sealed class WebcamSession
{
    public const byte ProtocolVersion = 0x01;

    /// <summary>Header bytes: version, flags, width, height, timestamp.</summary>
    public const int HeaderLength = 10;

    /// <summary>Whole-message cap (header + payload).</summary>
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    private const int InitialBufferBytes = 64 * 1024;
    private const byte KeyframeFlag = 0x01;
    private const int CodecShift = 1;
    private const int CodecMask = 0b11;

    private readonly WebSocket _socket;
    private readonly IWebcamFrameSink _sink;

    public WebcamSession(WebSocket socket, IWebcamFrameSink sink)
    {
        _socket = socket;
        _sink = sink;
    }

    public static WebcamFrameError TryParseFrame(ReadOnlySpan<byte> message, out WebcamFrameInfo info, out int payloadOffset)
    {
        info = default;
        payloadOffset = 0;
        if (message.Length > MaxFrameBytes)
            return WebcamFrameError.Oversize;
        if (message.Length < HeaderLength)
            return WebcamFrameError.TruncatedHeader;
        if (message[0] != ProtocolVersion)
            return WebcamFrameError.UnknownVersion;

        var flags = message[1];
        var codecBits = (flags >> CodecShift) & CodecMask;
        if (codecBits > (int)WebcamCodec.Mjpeg)
            return WebcamFrameError.UnknownCodec;

        var width = BinaryPrimitives.ReadUInt16LittleEndian(message.Slice(2, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(message.Slice(4, 2));
        var timestampMs = BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(6, 4));
        info = new WebcamFrameInfo(width, height, (flags & KeyframeFlag) != 0, (WebcamCodec)codecBits, timestampMs);
        payloadOffset = HeaderLength;
        return WebcamFrameError.None;
    }

    /// <summary>
    /// Pump the socket until close, cancel, or a protocol violation. Text
    /// messages are treated as keepalives and ignored.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(InitialBufferBytes);
        try
        {
            var filled = 0;
            while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await _socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer, filled, buffer.Length - filled), cancellationToken);
                }
                catch (WebSocketException)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                filled += result.Count;
                if (!result.EndOfMessage)
                {
                    if (filled >= MaxFrameBytes)
                    {
                        await CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame too large", cancellationToken);
                        return;
                    }
                    if (filled == buffer.Length)
                    {
                        var grown = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, MaxFrameBytes));
                        buffer.AsSpan(0, filled).CopyTo(grown);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = grown;
                    }
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    var error = TryParseFrame(buffer.AsSpan(0, filled), out var info, out var payloadOffset);
                    if (error != WebcamFrameError.None)
                    {
                        await CloseAsync(CloseStatusFor(error), CloseReasonFor(error), cancellationToken);
                        return;
                    }
                    var accepted = await _sink.OnFrameAsync(buffer.AsMemory(payloadOffset, filled - payloadOffset), info, cancellationToken);
                    if (!accepted)
                    {
                        await CloseAsync(WebSocketCloseStatus.ProtocolError, "format mismatch", cancellationToken);
                        return;
                    }
                }

                filled = 0;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static WebSocketCloseStatus CloseStatusFor(WebcamFrameError error) =>
        error == WebcamFrameError.Oversize ? WebSocketCloseStatus.MessageTooBig : WebSocketCloseStatus.ProtocolError;

    private static string CloseReasonFor(WebcamFrameError error) => error switch
    {
        WebcamFrameError.UnknownVersion => "unknown version",
        WebcamFrameError.UnknownCodec => "unknown codec",
        WebcamFrameError.Oversize => "frame too large",
        _ => "truncated frame",
    };

    private async Task CloseAsync(WebSocketCloseStatus status, string reason, CancellationToken cancellationToken)
    {
        try
        {
            // CloseOutputAsync: a silent peer must not park the handler and
            // pin the stream slot waiting for its close reply.
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _socket.CloseOutputAsync(status, reason, cancellationToken);
        }
        catch
        {
            // peer already gone
        }
    }
}
