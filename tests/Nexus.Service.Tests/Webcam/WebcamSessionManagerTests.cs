using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Nexus.Service.Models.Webcam;
using Nexus.Service.Webcam;

namespace Nexus.Service.Tests.Webcam;

public class WebcamSessionManagerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LongGrace = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ShortGrace = TimeSpan.FromMilliseconds(20);

    private static WebcamStartRequest MjpegRequest(int width = 1280, int height = 720) =>
        new() { Width = width, Height = height, Codec = "mjpeg" };

    [Fact]
    public async Task Start_ArmsCameraAndReportsStatus()
    {
        var camera = new FakeVirtualCamera();
        var manager = new WebcamSessionManager(camera, LongGrace);

        var (status, error) = await manager.StartAsync(MjpegRequest(1280, 720), CancellationToken.None);

        Assert.Null(error);
        Assert.NotNull(status);
        Assert.True(status.Active);
        Assert.False(status.Streaming);
        Assert.Equal(1280, status.Width);
        Assert.Equal(720, status.Height);
        Assert.Equal("mjpeg", status.Codec);
        Assert.Equal(0, status.FramesReceived);
        Assert.Equal("Nexus Camera", status.CameraName);
        Assert.Equal(1, camera.StartCount);
        Assert.Equal(new WebcamFormat(1280, 720, WebcamCodec.Mjpeg), camera.StartedFormat);
    }

    [Fact]
    public async Task Start_RejectsUnknownCodec()
    {
        var camera = new FakeVirtualCamera();
        var manager = new WebcamSessionManager(camera, LongGrace);

        var (status, error) = await manager.StartAsync(
            new WebcamStartRequest { Width = 640, Height = 480, Codec = "vp9" }, CancellationToken.None);

        Assert.Null(status);
        Assert.NotNull(error);
        Assert.Equal(0, camera.StartCount);
    }

    [Theory]
    [InlineData(0, 480)]
    [InlineData(640, 0)]
    [InlineData(-1, 480)]
    [InlineData(70000, 480)]
    public async Task Start_RejectsInvalidDimensions(int width, int height)
    {
        var camera = new FakeVirtualCamera();
        var manager = new WebcamSessionManager(camera, LongGrace);

        var (status, error) = await manager.StartAsync(
            new WebcamStartRequest { Width = width, Height = height, Codec = "mjpeg" }, CancellationToken.None);

        Assert.Null(status);
        Assert.NotNull(error);
        Assert.Equal(0, camera.StartCount);
    }

    [Fact]
    public async Task Start_RejectsCodecRequiringDecode()
    {
        var camera = new FakeVirtualCamera { Support = WebcamCodecSupport.RequiresDecode };
        var manager = new WebcamSessionManager(camera, LongGrace);

        var (status, error) = await manager.StartAsync(
            new WebcamStartRequest { Width = 640, Height = 480, Codec = "h264" }, CancellationToken.None);

        Assert.Null(status);
        Assert.NotNull(error);
        Assert.Contains("decoding", error);
        Assert.Equal(0, camera.StartCount);
    }

    [Fact]
    public async Task Start_RejectsUnsupportedCodec()
    {
        var camera = new FakeVirtualCamera { Support = WebcamCodecSupport.Unsupported };
        var manager = new WebcamSessionManager(camera, LongGrace);

        var (status, error) = await manager.StartAsync(MjpegRequest(), CancellationToken.None);

        Assert.Null(status);
        Assert.NotNull(error);
        Assert.Equal(0, camera.StartCount);
    }

    [Fact]
    public async Task Start_SurfacesCameraStartFailure()
    {
        var camera = new FakeVirtualCamera { StartException = new IOException("device busy") };
        var manager = new WebcamSessionManager(camera, LongGrace);

        var (status, error) = await manager.StartAsync(MjpegRequest(), CancellationToken.None);

        Assert.Null(status);
        Assert.Equal("device busy", error);
        Assert.False(manager.GetStatus().Active);
    }

    [Fact]
    public async Task Start_WhileActive_RestartsCameraWithNewFormat()
    {
        var camera = new FakeVirtualCamera();
        var manager = new WebcamSessionManager(camera, LongGrace);

        await manager.StartAsync(MjpegRequest(640, 480), CancellationToken.None);
        var (status, error) = await manager.StartAsync(MjpegRequest(1920, 1080), CancellationToken.None);

        Assert.Null(error);
        Assert.NotNull(status);
        Assert.Equal(1, camera.StopCount);
        Assert.Equal(2, camera.StartCount);
        Assert.Equal(new WebcamFormat(1920, 1080, WebcamCodec.Mjpeg), camera.StartedFormat);
    }

    [Fact]
    public void Status_DefaultsInactive()
    {
        var manager = new WebcamSessionManager(new FakeVirtualCamera(), LongGrace);

        var status = manager.GetStatus();

        Assert.False(status.Active);
        Assert.False(status.Streaming);
        Assert.Equal("", status.Codec);
        Assert.Equal("Nexus Camera", status.CameraName);
    }

    [Fact]
    public async Task Stream_BeforeStart_IsRefused()
    {
        var manager = new WebcamSessionManager(new FakeVirtualCamera(), LongGrace);
        var socket = new ScriptedWebSocket();

        await manager.HandleStreamSocketAsync(socket, CancellationToken.None).WaitAsync(TestTimeout);

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.SentCloseStatus);
        Assert.Equal("not started", socket.SentCloseDescription);
        Assert.False(manager.GetStatus().Streaming);
    }

    [Fact]
    public async Task Stream_ForwardsFramesToCamera()
    {
        var camera = new FakeVirtualCamera();
        var manager = new WebcamSessionManager(camera, LongGrace);
        await manager.StartAsync(MjpegRequest(width: 640, height: 480), CancellationToken.None);

        var socket = new ScriptedWebSocket();
        socket.EnqueueBinary(WebcamFrameParserTests.BuildFrame(
            keyframe: true, codecBits: 1, width: 640, height: 480, timestampMs: 99, payloadLength: 16));
        var handler = manager.HandleStreamSocketAsync(socket, CancellationToken.None);

        await camera.WaitForFramesAsync(1, TestTimeout);
        var status = manager.GetStatus();
        Assert.True(status.Streaming);
        Assert.Equal(1, status.FramesReceived);
        Assert.True(status.LastFrameUnixMs > 0);

        var (payload, info) = camera.Frames[0];
        Assert.Equal(16, payload.Length);
        Assert.Equal(1, payload[0]);
        Assert.True(info.Keyframe);
        Assert.Equal(WebcamCodec.Mjpeg, info.Codec);
        Assert.Equal(99u, info.TimestampMs);

        socket.EnqueueClientClose();
        await handler.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task SecondConnection_ReplacesFirst()
    {
        var camera = new FakeVirtualCamera();
        var manager = new WebcamSessionManager(camera, LongGrace);
        await manager.StartAsync(MjpegRequest(), CancellationToken.None);

        var first = new ScriptedWebSocket();
        first.EnqueueBinary(WebcamFrameParserTests.BuildFrame(codecBits: 1));
        var handler1 = manager.HandleStreamSocketAsync(first, CancellationToken.None);
        await camera.WaitForFramesAsync(1, TestTimeout);

        var second = new ScriptedWebSocket();
        var handler2 = manager.HandleStreamSocketAsync(second, CancellationToken.None);

        await first.Closed.Task.WaitAsync(TestTimeout);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, first.SentCloseStatus);
        Assert.Equal("replaced", first.SentCloseDescription);
        await handler1.WaitAsync(TestTimeout);

        var status = manager.GetStatus();
        Assert.True(status.Active);
        Assert.True(status.Streaming);
        Assert.Equal(0, camera.StopCount);

        second.EnqueueBinary(WebcamFrameParserTests.BuildFrame(codecBits: 1));
        await camera.WaitForFramesAsync(2, TestTimeout);

        second.EnqueueClientClose();
        await handler2.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Disconnect_StopsCameraAfterGrace()
    {
        var camera = new FakeVirtualCamera();
        var manager = new WebcamSessionManager(camera, ShortGrace);
        await manager.StartAsync(MjpegRequest(), CancellationToken.None);

        var socket = new ScriptedWebSocket();
        socket.EnqueueBinary(WebcamFrameParserTests.BuildFrame(codecBits: 1));
        var handler = manager.HandleStreamSocketAsync(socket, CancellationToken.None);
        await camera.WaitForFramesAsync(1, TestTimeout);

        socket.EnqueueClientClose();
        await handler.WaitAsync(TestTimeout);

        await camera.StoppedTcs.Task.WaitAsync(TestTimeout);
        Assert.False(manager.GetStatus().Active);
        Assert.False(manager.GetStatus().Streaming);
    }

    [Fact]
    public async Task CodecMismatch_ClosesStreamAsProtocolError()
    {
        var camera = new FakeVirtualCamera();
        var manager = new WebcamSessionManager(camera, ShortGrace);
        await manager.StartAsync(MjpegRequest(), CancellationToken.None);

        var socket = new ScriptedWebSocket();
        socket.EnqueueBinary(WebcamFrameParserTests.BuildFrame(codecBits: 0));
        var handler = manager.HandleStreamSocketAsync(socket, CancellationToken.None);
        await handler.WaitAsync(TestTimeout);

        Assert.Equal(WebSocketCloseStatus.ProtocolError, socket.SentCloseStatus);
        Assert.Equal("format mismatch", socket.SentCloseDescription);
        Assert.Empty(camera.Frames);
    }

    [Fact]
    public async Task DimensionMismatch_DropsFrameKeepsStream()
    {
        var camera = new FakeVirtualCamera();
        var manager = new WebcamSessionManager(camera, ShortGrace);
        await manager.StartAsync(MjpegRequest(), CancellationToken.None);

        var socket = new ScriptedWebSocket();
        socket.EnqueueBinary(WebcamFrameParserTests.BuildFrame(codecBits: 1, width: 720, height: 1280));
        socket.EnqueueBinary(WebcamFrameParserTests.BuildFrame(codecBits: 1));
        var handler = manager.HandleStreamSocketAsync(socket, CancellationToken.None);

        // Only the matching frame lands; the rotated one is dropped silently.
        await camera.WaitForFramesAsync(1, TestTimeout);
        Assert.True(manager.GetStatus().Streaming);

        socket.EnqueueClientClose();
        await handler.WaitAsync(TestTimeout);
        Assert.Single(camera.Frames);
    }

    [Fact]
    public async Task Reconnect_WithinGrace_KeepsCamera()
    {
        var camera = new FakeVirtualCamera();
        var manager = new WebcamSessionManager(camera, LongGrace);
        await manager.StartAsync(MjpegRequest(), CancellationToken.None);

        var first = new ScriptedWebSocket();
        first.EnqueueBinary(WebcamFrameParserTests.BuildFrame(codecBits: 1));
        var handler1 = manager.HandleStreamSocketAsync(first, CancellationToken.None);
        await camera.WaitForFramesAsync(1, TestTimeout);
        first.EnqueueClientClose();
        await handler1.WaitAsync(TestTimeout);

        var second = new ScriptedWebSocket();
        second.EnqueueBinary(WebcamFrameParserTests.BuildFrame(codecBits: 1));
        var handler2 = manager.HandleStreamSocketAsync(second, CancellationToken.None);
        await camera.WaitForFramesAsync(2, TestTimeout);

        Assert.Equal(0, camera.StopCount);
        var status = manager.GetStatus();
        Assert.True(status.Active);
        Assert.True(status.Streaming);

        second.EnqueueClientClose();
        await handler2.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Stop_ClosesStreamSocketAndCamera()
    {
        var camera = new FakeVirtualCamera();
        var manager = new WebcamSessionManager(camera, LongGrace);
        await manager.StartAsync(MjpegRequest(), CancellationToken.None);

        var socket = new ScriptedWebSocket();
        socket.EnqueueBinary(WebcamFrameParserTests.BuildFrame(codecBits: 1));
        var handler = manager.HandleStreamSocketAsync(socket, CancellationToken.None);
        await camera.WaitForFramesAsync(1, TestTimeout);

        var status = await manager.StopAsync().WaitAsync(TestTimeout);

        Assert.False(status.Active);
        Assert.False(status.Streaming);
        Assert.Equal(1, camera.StopCount);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.SentCloseStatus);
        Assert.Equal("stopped", socket.SentCloseDescription);
        await handler.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task UnknownVersion_ClosesProtocolError()
    {
        var (camera, socket, handler) = await ConnectAsync(
            WebcamFrameParserTests.BuildFrame(version: 0x02, codecBits: 1));

        await handler.WaitAsync(TestTimeout);

        Assert.Equal(WebSocketCloseStatus.ProtocolError, socket.SentCloseStatus);
        Assert.Equal("unknown version", socket.SentCloseDescription);
        Assert.Empty(camera.Frames);
    }

    [Fact]
    public async Task UnknownCodec_ClosesProtocolError()
    {
        var (camera, socket, handler) = await ConnectAsync(
            WebcamFrameParserTests.BuildFrame(codecBits: 3));

        await handler.WaitAsync(TestTimeout);

        Assert.Equal(WebSocketCloseStatus.ProtocolError, socket.SentCloseStatus);
        Assert.Equal("unknown codec", socket.SentCloseDescription);
        Assert.Empty(camera.Frames);
    }

    [Fact]
    public async Task TruncatedHeader_ClosesProtocolError()
    {
        var (camera, socket, handler) = await ConnectAsync(new byte[] { WebcamSession.ProtocolVersion, 0x02, 0x00 });

        await handler.WaitAsync(TestTimeout);

        Assert.Equal(WebSocketCloseStatus.ProtocolError, socket.SentCloseStatus);
        Assert.Equal("truncated frame", socket.SentCloseDescription);
        Assert.Empty(camera.Frames);
    }

    [Fact]
    public async Task OversizedFrame_ClosesMessageTooBig()
    {
        var (camera, socket, handler) = await ConnectAsync(new byte[WebcamSession.MaxFrameBytes + 1]);

        await handler.WaitAsync(TestTimeout);

        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.SentCloseStatus);
        Assert.Equal("frame too large", socket.SentCloseDescription);
        Assert.Empty(camera.Frames);
    }

    [Fact]
    public async Task TextMessages_AreIgnoredAsKeepalives()
    {
        var camera = new FakeVirtualCamera();
        var manager = new WebcamSessionManager(camera, LongGrace);
        await manager.StartAsync(MjpegRequest(), CancellationToken.None);

        var socket = new ScriptedWebSocket();
        socket.EnqueueText("ping");
        socket.EnqueueBinary(WebcamFrameParserTests.BuildFrame(codecBits: 1));
        var handler = manager.HandleStreamSocketAsync(socket, CancellationToken.None);

        await camera.WaitForFramesAsync(1, TestTimeout);
        Assert.Null(socket.SentCloseStatus);

        socket.EnqueueClientClose();
        await handler.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task AbruptDisconnect_StopsCameraAfterGrace()
    {
        var camera = new FakeVirtualCamera();
        var manager = new WebcamSessionManager(camera, ShortGrace);
        await manager.StartAsync(MjpegRequest(), CancellationToken.None);

        var socket = new ScriptedWebSocket();
        socket.EnqueueBinary(WebcamFrameParserTests.BuildFrame(codecBits: 1));
        var handler = manager.HandleStreamSocketAsync(socket, CancellationToken.None);
        await camera.WaitForFramesAsync(1, TestTimeout);

        socket.AbortFromClient();
        await handler.WaitAsync(TestTimeout);

        await camera.StoppedTcs.Task.WaitAsync(TestTimeout);
        Assert.False(manager.GetStatus().Active);
    }

    private static async Task<(FakeVirtualCamera Camera, ScriptedWebSocket Socket, Task Handler)> ConnectAsync(byte[] firstMessage)
    {
        var camera = new FakeVirtualCamera();
        var manager = new WebcamSessionManager(camera, LongGrace);
        await manager.StartAsync(MjpegRequest(), CancellationToken.None);

        var socket = new ScriptedWebSocket();
        socket.EnqueueBinary(firstMessage);
        var handler = manager.HandleStreamSocketAsync(socket, CancellationToken.None);
        return (camera, socket, handler);
    }

    private sealed class FakeVirtualCamera : IVirtualCamera
    {
        private readonly object _lock = new();
        private (int Count, TaskCompletionSource Tcs)? _frameWait;

        public string Name => WebcamDefaults.CameraName;
        public WebcamCodecSupport Support { get; set; } = WebcamCodecSupport.Passthrough;
        public Exception? StartException { get; set; }
        public WebcamFormat? StartedFormat { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public List<(byte[] Payload, WebcamFrameInfo Info)> Frames { get; } = new();
        public TaskCompletionSource StoppedTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WebcamCodecSupport GetCodecSupport(WebcamCodec codec) => Support;

        public Task StartAsync(WebcamFormat format, CancellationToken cancellationToken)
        {
            if (StartException is not null)
                throw StartException;
            lock (_lock)
            {
                StartCount++;
                StartedFormat = format;
            }
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            lock (_lock)
            {
                StopCount++;
            }
            StoppedTcs.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask WriteFrameAsync(ReadOnlyMemory<byte> payload, in WebcamFrameInfo info)
        {
            lock (_lock)
            {
                Frames.Add((payload.ToArray(), info));
                if (_frameWait is { } wait && Frames.Count >= wait.Count)
                {
                    _frameWait = null;
                    wait.Tcs.TrySetResult();
                }
            }
            return ValueTask.CompletedTask;
        }

        public Task WaitForFramesAsync(int count, TimeSpan timeout)
        {
            lock (_lock)
            {
                if (Frames.Count >= count)
                    return Task.CompletedTask;
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _frameWait = (count, tcs);
                return tcs.Task.WaitAsync(timeout);
            }
        }
    }

    /// <summary>
    /// Test transport: the "phone" side enqueues messages; receive fragments
    /// them to the caller's buffer size like a real socket. Server-sent closes
    /// record the first status/description and unblock any pending receive.
    /// </summary>
    private sealed class ScriptedWebSocket : WebSocket
    {
        private readonly Channel<(WebSocketMessageType Type, byte[] Data)> _incoming =
            Channel.CreateUnbounded<(WebSocketMessageType, byte[])>();
        private (WebSocketMessageType Type, byte[] Data)? _pending;
        private int _pendingOffset;
        private WebSocketState _state = WebSocketState.Open;

        public WebSocketCloseStatus? SentCloseStatus { get; private set; }
        public string? SentCloseDescription { get; private set; }
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void EnqueueBinary(byte[] data) =>
            _incoming.Writer.TryWrite((WebSocketMessageType.Binary, data));

        public void EnqueueText(string text) =>
            _incoming.Writer.TryWrite((WebSocketMessageType.Text, Encoding.UTF8.GetBytes(text)));

        public void EnqueueClientClose() =>
            _incoming.Writer.TryWrite((WebSocketMessageType.Close, Array.Empty<byte>()));

        /// <summary>Connection drop without a close frame: pending receives throw.</summary>
        public void AbortFromClient() => _incoming.Writer.TryComplete();

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_pending is null)
            {
                (WebSocketMessageType Type, byte[] Data) item;
                try
                {
                    item = await _incoming.Reader.ReadAsync(cancellationToken);
                }
                catch (ChannelClosedException)
                {
                    throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
                }
                if (item.Type == WebSocketMessageType.Close)
                {
                    _state = WebSocketState.CloseReceived;
                    return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
                }
                _pending = item;
                _pendingOffset = 0;
            }

            var (type, data) = _pending.Value;
            var take = Math.Min(buffer.Count, data.Length - _pendingOffset);
            Array.Copy(data, _pendingOffset, buffer.Array!, buffer.Offset, take);
            _pendingOffset += take;
            var end = _pendingOffset >= data.Length;
            if (end)
                _pending = null;
            return new WebSocketReceiveResult(take, type, end);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseCore(closeStatus, statusDescription);

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseCore(closeStatus, statusDescription);

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => SentCloseStatus;
        public override string? CloseStatusDescription => SentCloseDescription;
        public override string? SubProtocol => null;

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
            _incoming.Writer.TryComplete();
        }

        public override void Dispose()
        {
        }

        private Task CloseCore(WebSocketCloseStatus closeStatus, string? statusDescription)
        {
            if (SentCloseStatus is null)
            {
                SentCloseStatus = closeStatus;
                SentCloseDescription = statusDescription;
            }
            _state = WebSocketState.Closed;
            Closed.TrySetResult();
            _incoming.Writer.TryComplete();
            return Task.CompletedTask;
        }
    }
}
