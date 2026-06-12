using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Webcam;

namespace Nexus.Service.Webcam;

/// <summary>
/// Singleton owner of the phone-as-webcam lifecycle: the armed format, the
/// single active stream socket, the frame counters, and the virtual camera
/// device. Arm via the start endpoint, stream via the binary socket, disarm
/// via the stop endpoint.
///
/// Exactly one phone streams at a time: a new connection replaces the current
/// one (the old socket is closed normally with a "replaced" reason). On stream
/// disconnect the camera stays up for a short grace window - a CTS-cancelled
/// delay, not a poll - so a phone reconnect doesn't bounce the OS device.
/// </summary>
public sealed class WebcamSessionManager : IWebcamFrameSink
{
    private static readonly TimeSpan DefaultDisconnectGrace = TimeSpan.FromSeconds(5);

    private readonly IVirtualCamera _camera;
    private readonly TimeSpan _disconnectGrace;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Reference, not WebcamFormat?: the frame path reads it lock-free and
    // Nullable<struct> reads can tear; reference reads are atomic.
    private ArmedFormat? _format;

    private sealed record ArmedFormat(int Width, int Height, WebcamCodec Codec);
    private StreamSlot? _stream;
    private CancellationTokenSource? _graceCts;
    private long _framesReceived;
    private long _framesDropped;
    private long _lastFrameUnixMs;

    public WebcamSessionManager(IVirtualCamera camera) : this(camera, DefaultDisconnectGrace) { }

    public WebcamSessionManager(IVirtualCamera camera, TimeSpan disconnectGrace)
    {
        _camera = camera;
        _disconnectGrace = disconnectGrace;
    }

    /// <summary>Arm the session intent and start the virtual camera. Restarting while active re-creates the device with the new format.</summary>
    public async Task<(WebcamStatusResponse? Status, string? Error)> StartAsync(WebcamStartRequest request, CancellationToken cancellationToken)
    {
        if (!TryParseCodec(request.Codec, out var codec))
            return (null, "unknown codec (expected h264 or mjpeg)");
        if (request.Width <= 0 || request.Height <= 0 || request.Width > ushort.MaxValue || request.Height > ushort.MaxValue)
            return (null, "invalid dimensions");

        switch (_camera.GetCodecSupport(codec))
        {
            case WebcamCodecSupport.Passthrough:
                break;
            case WebcamCodecSupport.RequiresDecode:
                return (null, $"{CodecName(codec)} requires decoding, which is not supported on this platform yet");
            default:
                return (null, $"{CodecName(codec)} is not supported by the virtual camera on this platform");
        }

        var format = new WebcamFormat(request.Width, request.Height, codec);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            CancelGraceLocked();
            if (_format is not null)
            {
                _format = null;
                try
                {
                    await _camera.StopAsync();
                }
                catch
                {
                    // old device teardown is best-effort
                }
            }
            try
            {
                await _camera.StartAsync(format, cancellationToken);
            }
            catch (Exception ex)
            {
                return (null, ex.Message);
            }
            _format = new ArmedFormat(format.Width, format.Height, format.Codec);
            Interlocked.Exchange(ref _framesReceived, 0);
            Interlocked.Exchange(ref _framesDropped, 0);
            Interlocked.Exchange(ref _lastFrameUnixMs, 0);
            return (BuildStatus(), null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stop the camera and close any active stream socket.</summary>
    public async Task<WebcamStatusResponse> StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            CancelGraceLocked();
            var stream = _stream;
            _stream = null;
            if (stream is not null)
                await stream.CloseAsync(WebSocketCloseStatus.NormalClosure, "stopped");
            if (_format is not null)
            {
                _format = null;
                try
                {
                    await _camera.StopAsync();
                }
                catch
                {
                    // device teardown is best-effort
                }
            }
            return BuildStatus();
        }
        finally
        {
            _gate.Release();
        }
    }

    public WebcamStatusResponse GetStatus() => BuildStatus();

    /// <summary>
    /// Own a newly-upgraded stream socket until disconnect. Refused (policy
    /// violation close) when no start call has armed a session; an existing
    /// stream is cleanly replaced.
    /// </summary>
    public async Task HandleStreamSocketAsync(WebSocket socket, CancellationToken requestAborted)
    {
        StreamSlot slot;
        await _gate.WaitAsync(requestAborted);
        try
        {
            if (_format is null)
            {
                try
                {
                    // CloseOutputAsync, not CloseAsync: the full handshake waits
                    // for the peer's close reply and would pin the module gate
                    // on a dead phone.
                    await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "not started", CancellationToken.None);
                }
                catch
                {
                    // peer already gone
                }
                return;
            }
            CancelGraceLocked();
            var old = _stream;
            _stream = null;
            if (old is not null)
                await old.CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced");
            slot = new StreamSlot(socket, requestAborted);
            _stream = slot;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            var session = new WebcamSession(socket, this);
            await session.RunAsync(slot.Cts.Token);
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                if (_stream == slot)
                {
                    _stream = null;
                    if (_format is not null)
                        StartGraceLocked();
                }
            }
            finally
            {
                _gate.Release();
            }
            // Complete the close handshake when the phone initiated it.
            try
            {
                if (socket.State == WebSocketState.CloseReceived)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch
            {
                // peer already gone
            }
            slot.Dispose();
        }
    }

    async ValueTask<bool> IWebcamFrameSink.OnFrameAsync(ReadOnlyMemory<byte> payload, WebcamFrameInfo info, CancellationToken cancellationToken)
    {
        // Reference read is atomic; a raced stop just rejects the frame.
        var format = _format;
        if (format is null || info.Codec != format.Codec)
            return false;
        if (info.Width != format.Width || info.Height != format.Height)
        {
            // Tolerated, not fatal: rotation mid-stream re-arms with new
            // dimensions; writing a mismatched frame would corrupt consumers.
            Interlocked.Increment(ref _framesDropped);
            return true;
        }
        Interlocked.Increment(ref _framesReceived);
        Interlocked.Exchange(ref _lastFrameUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await _camera.WriteFrameAsync(payload, in info);
        return true;
    }

    internal static bool TryParseCodec(string? value, out WebcamCodec codec)
    {
        if (string.Equals(value, "h264", StringComparison.OrdinalIgnoreCase))
        {
            codec = WebcamCodec.H264;
            return true;
        }
        if (string.Equals(value, "mjpeg", StringComparison.OrdinalIgnoreCase))
        {
            codec = WebcamCodec.Mjpeg;
            return true;
        }
        codec = default;
        return false;
    }

    internal static string CodecName(WebcamCodec codec) => codec == WebcamCodec.Mjpeg ? "mjpeg" : "h264";

    private WebcamStatusResponse BuildStatus()
    {
        var format = _format;
        return new WebcamStatusResponse
        {
            Active = format is not null,
            Streaming = _stream is not null,
            Width = format?.Width ?? 0,
            Height = format?.Height ?? 0,
            Codec = format is { } f ? CodecName(f.Codec) : "",
            FramesReceived = Interlocked.Read(ref _framesReceived),
            LastFrameUnixMs = Interlocked.Read(ref _lastFrameUnixMs),
            CameraName = _camera.Name,
        };
    }

    private void CancelGraceLocked()
    {
        if (_graceCts is { } cts)
        {
            _graceCts = null;
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void StartGraceLocked()
    {
        var cts = new CancellationTokenSource();
        _graceCts = cts;
        _ = StopAfterGraceAsync(cts);
    }

    private async Task StopAfterGraceAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_disconnectGrace, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            // A replace/start/stop may have raced the delay; only the still-current
            // timer with no reconnected stream tears the camera down.
            if (_graceCts != cts)
                return;
            _graceCts = null;
            cts.Dispose();
            if (_stream is not null || _format is null)
                return;
            _format = null;
            try
            {
                await _camera.StopAsync();
            }
            catch
            {
                // device teardown is best-effort
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Active socket plus the CTS that aborts its receive loop. Closing sends
    /// the close frame without waiting for the peer's reply (a dead phone must
    /// not stall the gate), then cancels the loop.
    /// </summary>
    private sealed class StreamSlot : IDisposable
    {
        private readonly WebSocket _socket;

        public StreamSlot(WebSocket socket, CancellationToken requestAborted)
        {
            _socket = socket;
            Cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        }

        public CancellationTokenSource Cts { get; }

        public async Task CloseAsync(WebSocketCloseStatus status, string reason)
        {
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await _socket.CloseOutputAsync(status, reason, CancellationToken.None);
            }
            catch
            {
                // peer already gone
            }
            Cts.Cancel();
        }

        public void Dispose() => Cts.Dispose();
    }
}
