using System.Buffers.Binary;
using Nexus.Service.Webcam;
using Nexus.Service.Webcam.Mac;

namespace Nexus.Service.Tests.Webcam;

/// <summary>
/// Lifecycle of the macOS camera through the activator/launcher seams - runs
/// anywhere, no macOS APIs or processes touched.
/// </summary>
public class CmioCameraTests
{
    private static readonly WebcamFormat Format = new(1280, 720, WebcamCodec.H264);

    private static CmioCamera Make(FakeLauncher launcher, FakeActivator? activator = null) =>
        new(activator ?? new FakeActivator(), launcher);

    [Fact]
    public async Task Start_WaitsForReadyLine_IgnoringChatter()
    {
        var helper = new FakeHelper("starting", CmioCamera.ReadyLine);
        var launcher = new FakeLauncher(helper);
        var camera = Make(launcher);

        await camera.StartAsync(Format, CancellationToken.None);

        Assert.Equal(1, launcher.Launches);
        Assert.DoesNotContain("kill", helper.Ops);
        Assert.DoesNotContain("dispose", helper.Ops);
    }

    [Fact]
    public async Task Start_HelperExit_SurfacesExitCodeMessageAndStderr()
    {
        var helper = new FakeHelper
        {
            HasExited = true,
            ExitCode = CameraHelperExit.DeviceNotFound,
            StandardErrorSnapshot = "no device matching uid",
        };
        var camera = Make(new FakeLauncher(helper));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => camera.StartAsync(Format, CancellationToken.None));

        Assert.Contains("camera extension is not running", ex.Message);
        Assert.Contains("no device matching uid", ex.Message);
        Assert.Contains("dispose", helper.Ops);
    }

    [Fact]
    public async Task Start_ActivationPendingApproval_ThrowsBeforeLaunching()
    {
        var activator = new FakeActivator
        {
            Status = CameraExtensionState.NotInstalled,
            ActivateResult = CameraExtensionState.PendingApproval,
        };
        var launcher = new FakeLauncher(new FakeHelper(CmioCamera.ReadyLine));
        var camera = Make(launcher, activator);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => camera.StartAsync(Format, CancellationToken.None));

        Assert.Contains("System Settings > General > Login Items & Extensions", ex.Message);
        Assert.Equal(1, activator.ActivateCalls);
        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task Start_ActivationRebootRequired_Throws()
    {
        var activator = new FakeActivator
        {
            Status = CameraExtensionState.NotInstalled,
            ActivateResult = CameraExtensionState.RebootRequired,
        };
        var camera = Make(new FakeLauncher(), activator);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => camera.StartAsync(Format, CancellationToken.None));

        Assert.Contains("reboot", ex.Message);
    }

    [Fact]
    public async Task Start_AlreadyInstalled_SkipsActivation()
    {
        var activator = new FakeActivator { Status = CameraExtensionState.Installed };
        var camera = Make(new FakeLauncher(new FakeHelper(CmioCamera.ReadyLine)), activator);

        await camera.StartAsync(Format, CancellationToken.None);

        Assert.Equal(0, activator.ActivateCalls);
    }

    [Fact]
    public async Task WriteFrame_StdinBytes_MatchTheFrameProtocolExactly()
    {
        var helper = new FakeHelper(CmioCamera.ReadyLine);
        var camera = Make(new FakeLauncher(helper));
        await camera.StartAsync(new WebcamFormat(1280, 720, WebcamCodec.Mjpeg), CancellationToken.None);

        var payload = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 };
        var info = new WebcamFrameInfo(1280, 720, Keyframe: true, WebcamCodec.Mjpeg, 123456789u);
        await camera.WriteFrameAsync(payload, in info);

        var bytes = helper.Stdin.ToArray();
        Assert.Equal(CmioCamera.FrameHeaderLength + payload.Length, bytes.Length);
        Assert.Equal(WebcamSession.ProtocolVersion, bytes[0]);
        var expectedFlags = (byte)(((int)WebcamCodec.Mjpeg << 1) | 0x01);
        Assert.Equal(expectedFlags, bytes[1]);
        Assert.Equal(1280, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2)));
        Assert.Equal(720, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)));
        Assert.Equal(123456789u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(6, 4)));
        Assert.Equal((uint)payload.Length, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(10, 4)));
        Assert.Equal(payload, bytes[CmioCamera.FrameHeaderLength..]);

        // The leading bytes are the stream socket header plus payload length;
        // they must round-trip through the canonical parser.
        var parsed = WebcamSession.TryParseFrame(
            bytes.AsSpan(0, WebcamSession.HeaderLength), out var parsedInfo, out _);
        Assert.Equal(WebcamFrameError.None, parsed);
        Assert.Equal(info, parsedInfo);
    }

    [Fact]
    public async Task WriteBeforeStart_IsIgnored()
    {
        var launcher = new FakeLauncher();
        var camera = Make(launcher);

        await camera.WriteFrameAsync(new byte[8], new WebcamFrameInfo(1280, 720, true, WebcamCodec.H264, 0));

        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task Stop_ClosesStdin_WaitsForExit_ThenKills()
    {
        var helper = new FakeHelper(CmioCamera.ReadyLine) { WaitForExitResult = false };
        var camera = Make(new FakeLauncher(helper));
        await camera.StartAsync(Format, CancellationToken.None);

        await camera.StopAsync();

        Assert.Equal(new[] { "eof", "wait", "kill", "dispose" }, helper.Ops);
    }

    [Fact]
    public async Task Stop_CleanExit_DoesNotKill_AndIsIdempotent()
    {
        var helper = new FakeHelper(CmioCamera.ReadyLine) { WaitForExitResult = true };
        var camera = Make(new FakeLauncher(helper));
        await camera.StartAsync(Format, CancellationToken.None);

        await camera.StopAsync();
        await camera.StopAsync();

        Assert.Equal(new[] { "eof", "wait", "dispose" }, helper.Ops);
    }

    [Fact]
    public async Task HelperDeath_MidSession_DegradesWithoutThrowing()
    {
        var broken = new BrokenPipeStream();
        var helper = new FakeHelper(CmioCamera.ReadyLine) { StdinOverride = broken };
        var camera = Make(new FakeLauncher(helper));
        await camera.StartAsync(Format, CancellationToken.None);

        var info = new WebcamFrameInfo(1280, 720, true, WebcamCodec.H264, 0);
        await camera.WriteFrameAsync(new byte[16], in info);
        await camera.WriteFrameAsync(new byte[16], in info);

        Assert.Equal(1, broken.WriteAttempts);
    }

    [Fact]
    public async Task HelperExited_MidSession_SkipsWritesWithoutThrowing()
    {
        var helper = new FakeHelper(CmioCamera.ReadyLine);
        var camera = Make(new FakeLauncher(helper));
        await camera.StartAsync(Format, CancellationToken.None);
        helper.HasExited = true;

        var info = new WebcamFrameInfo(1280, 720, true, WebcamCodec.H264, 0);
        await camera.WriteFrameAsync(new byte[16], in info);

        Assert.Equal(0, helper.Stdin.Length);
    }

    [Fact]
    public async Task Restart_ReplacesTheHelper()
    {
        var first = new FakeHelper(CmioCamera.ReadyLine);
        var second = new FakeHelper(CmioCamera.ReadyLine);
        var launcher = new FakeLauncher(first, second);
        var camera = Make(launcher);

        await camera.StartAsync(Format, CancellationToken.None);
        await camera.StartAsync(Format, CancellationToken.None);

        Assert.Equal(2, launcher.Launches);
        Assert.Contains("eof", first.Ops);
        Assert.Contains("dispose", first.Ops);

        var info = new WebcamFrameInfo(1280, 720, true, WebcamCodec.H264, 0);
        await camera.WriteFrameAsync(new byte[4], in info);
        Assert.Equal(0, first.Stdin.Length);
        Assert.True(second.Stdin.Length > 0);
    }

    private sealed class FakeActivator : ICameraExtensionActivator
    {
        public CameraExtensionState Status { get; set; } = CameraExtensionState.Installed;
        public CameraExtensionState ActivateResult { get; set; } = CameraExtensionState.Installed;
        public int ActivateCalls { get; private set; }

        public CameraExtensionState GetStatus() => Status;

        public CameraExtensionState Activate()
        {
            ActivateCalls++;
            return ActivateResult;
        }
    }

    private sealed class FakeLauncher : ICameraHelperLauncher
    {
        private readonly Queue<ICameraHelperProcess> _queue = new();

        public FakeLauncher(params ICameraHelperProcess[] helpers)
        {
            foreach (var helper in helpers)
                _queue.Enqueue(helper);
        }

        public int Launches { get; private set; }

        public ICameraHelperProcess Launch()
        {
            Launches++;
            return _queue.Dequeue();
        }
    }

    private sealed class FakeHelper : ICameraHelperProcess
    {
        private readonly Queue<string?> _stdoutLines = new();

        public FakeHelper(params string?[] stdoutLines)
        {
            foreach (var line in stdoutLines)
                _stdoutLines.Enqueue(line);
        }

        public List<string> Ops { get; } = new();
        public MemoryStream Stdin { get; } = new();
        public Stream? StdinOverride { get; set; }
        public bool HasExited { get; set; }
        public int ExitCode { get; set; }
        public string StandardErrorSnapshot { get; set; } = "";
        public bool WaitForExitResult { get; set; } = true;

        public Stream StandardInput => StdinOverride ?? Stdin;

        public ValueTask<string?> ReadOutputLineAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(_stdoutLines.Count > 0 ? _stdoutLines.Dequeue() : null);

        public void CloseStandardInput() => Ops.Add("eof");

        public bool WaitForExit(TimeSpan timeout)
        {
            Ops.Add("wait");
            return WaitForExitResult;
        }

        public void Kill() => Ops.Add("kill");

        public void Dispose() => Ops.Add("dispose");
    }

    private sealed class BrokenPipeStream : Stream
    {
        public int WriteAttempts { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteAttempts++;
            throw new IOException("broken pipe");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAttempts++;
            throw new IOException("broken pipe");
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
