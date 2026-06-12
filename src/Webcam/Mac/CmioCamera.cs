using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform;

namespace Nexus.Service.Webcam.Mac;

/// <summary>
/// macOS virtual camera backed by the bundled CMIO camera extension. Start
/// ensures the system extension is activated (via the activator dylib), then
/// spawns the camera helper sidecar and waits for its ready line; the helper
/// decodes H.264/MJPEG and feeds the extension's sink stream. Both stream
/// codecs are reported as passthrough because the decode stage lives behind
/// this sink, mirroring the Windows camera. Frames go to the helper stdin as
/// [u8 ver][u8 flags][u16 w][u16 h][u32 tsMs][u32 payloadLen][payload],
/// little-endian; ver and the flag bit layout are shared with the stream
/// socket header in <see cref="WebcamSession"/>. A helper death mid-session
/// degrades to dropped frames instead of throwing; the next start replaces it.
/// </summary>
public sealed class CmioCamera : IVirtualCamera
{
    /// <summary>Stdin frame header bytes: version, flags, width, height, timestamp, payload length.</summary>
    internal const int FrameHeaderLength = 14;

    /// <summary>Stdout line the helper prints once the sink stream accepts frames.</summary>
    internal const string ReadyLine = "READY";

    // Must match the WebcamSession flag bit layout.
    private const byte KeyframeFlag = 0x01;
    private const int CodecShift = 1;

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(2);

    private readonly ICameraExtensionActivator _activator;
    private readonly ICameraHelperLauncher _launcher;
    // Serializes lifecycle against the frame path and guards the header buffer.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly byte[] _header = new byte[FrameHeaderLength];

    private ICameraHelperProcess? _helper;
    private bool _helperFailed;

    public CmioCamera() : this(CreateActivator(), CreateLauncher()) { }

    internal CmioCamera(ICameraExtensionActivator activator, ICameraHelperLauncher launcher)
    {
        _activator = activator;
        _launcher = launcher;
    }

    public string Name => WebcamDefaults.CameraName;

    public WebcamCodecSupport GetCodecSupport(WebcamCodec codec) => codec switch
    {
        WebcamCodec.H264 or WebcamCodec.Mjpeg => WebcamCodecSupport.Passthrough,
        _ => WebcamCodecSupport.Unsupported,
    };

    public async Task StartAsync(WebcamFormat format, CancellationToken cancellationToken)
    {
        if (GetCodecSupport(format.Codec) != WebcamCodecSupport.Passthrough)
            throw new NotSupportedException($"codec {format.Codec} is not supported by the macOS virtual camera");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            StopHelperLocked();
            EnsureExtensionReady();

            // The armed format is not passed to the helper; every stdin frame
            // header carries its own geometry and codec.
            var pending = _launcher.Launch();
            try
            {
                await WaitForReadyAsync(pending, cancellationToken);
                _helper = pending;
                _helperFailed = false;
                pending = null;
            }
            finally
            {
                if (pending is not null)
                {
                    pending.Kill();
                    pending.Dispose();
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            StopHelperLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask WriteFrameAsync(ReadOnlyMemory<byte> payload, in WebcamFrameInfo info)
    {
        if (payload.Length == 0)
            return ValueTask.CompletedTask;
        return new ValueTask(WriteFrameCoreAsync(payload, info));
    }

    private async Task WriteFrameCoreAsync(ReadOnlyMemory<byte> payload, WebcamFrameInfo info)
    {
        await _gate.WaitAsync();
        try
        {
            var helper = _helper;
            if (helper is null || _helperFailed)
                return;
            if (helper.HasExited)
            {
                MarkDeadLocked(helper);
                return;
            }

            FillFrameHeader(_header, in info, payload.Length);
            try
            {
                // Bound the write: a helper that is alive but not draining
                // stdin (wedged decode, full pipe) would otherwise stall the
                // shared gate and block Stop/Start. A timed-out write marks
                // the helper dead so the next start replaces it.
                using var timeout = new CancellationTokenSource(WriteTimeout);
                var stdin = helper.StandardInput;
                await stdin.WriteAsync(_header, timeout.Token);
                await stdin.WriteAsync(payload, timeout.Token);
                await stdin.FlushAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // A dead or wedged helper must not tear down the stream; frames
                // are dropped until the next start replaces the process.
                MarkDeadLocked(helper);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Layout shared with the Swift helper's stdin parser; see the class doc.</summary>
    internal static void FillFrameHeader(Span<byte> header, in WebcamFrameInfo info, int payloadLength)
    {
        header[0] = WebcamSession.ProtocolVersion;
        var flags = (byte)((int)info.Codec << CodecShift);
        if (info.Keyframe)
            flags |= KeyframeFlag;
        header[1] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(2, 2), (ushort)info.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(4, 2), (ushort)info.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(6, 4), info.TimestampMs);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(10, 4), (uint)payloadLength);
    }

    private void EnsureExtensionReady()
    {
        var state = _activator.GetStatus();
        if (state == CameraExtensionState.Installed)
            return;
        // Re-submitting an activation is how the approval prompt re-surfaces
        // for every non-enabled state, including a stale installed version.
        state = _activator.Activate();
        if (state == CameraExtensionState.Installed)
            return;
        throw state switch
        {
            CameraExtensionState.PendingApproval or CameraExtensionState.Disabled => new InvalidOperationException(
                "Nexus Camera extension is awaiting approval: open System Settings > General > Login Items & Extensions, allow the Nexus camera extension, then start the webcam again"),
            CameraExtensionState.RebootRequired => new InvalidOperationException(
                "Nexus Camera extension is installed but needs a restart: reboot your Mac, then start the webcam again"),
            _ => new InvalidOperationException(
                $"Nexus Camera extension activation failed (state: {state}); reinstall Nexus or inspect 'systemextensionsctl list'"),
        };
    }

    private static async Task WaitForReadyAsync(ICameraHelperProcess helper, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadyTimeout);
        try
        {
            while (true)
            {
                var line = await helper.ReadOutputLineAsync(timeout.Token)
                    ?? throw BuildExitError(helper);
                if (line.Trim() == ReadyLine)
                    return;
                // pre-ready chatter is ignored
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var stderr = helper.StandardErrorSnapshot;
            throw new InvalidOperationException(stderr.Length > 0
                ? $"camera helper did not signal ready within {ReadyTimeout.TotalSeconds:0}s (helper stderr: {stderr})"
                : $"camera helper did not signal ready within {ReadyTimeout.TotalSeconds:0}s");
        }
    }

    private static InvalidOperationException BuildExitError(ICameraHelperProcess helper)
    {
        // Stdout EOF can precede process exit by a beat; wait so the exit code is real.
        helper.WaitForExit(ExitGrace);
        var reason = helper.HasExited
            ? CameraHelperExit.Describe(helper.ExitCode)
            : "camera helper closed its output without signaling ready";
        var stderr = helper.StandardErrorSnapshot;
        return new InvalidOperationException(stderr.Length > 0 ? $"{reason} (helper stderr: {stderr})" : reason);
    }

    private void MarkDeadLocked(ICameraHelperProcess helper)
    {
        if (_helperFailed)
            return;
        _helperFailed = true;
        var detail = helper.HasExited ? $"exit code {helper.ExitCode}" : "stdin pipe closed";
        var stderr = helper.StandardErrorSnapshot;
        ServiceLog.Warn(stderr.Length > 0
            ? $"[webcam] camera helper died mid-session ({detail}): {stderr}"
            : $"[webcam] camera helper died mid-session ({detail})");
    }

    private void StopHelperLocked()
    {
        var helper = _helper;
        _helper = null;
        _helperFailed = false;
        if (helper is null)
            return;
        try
        {
            // EOF first so the helper drains and exits cleanly; kill is the
            // fallback. Only the process this instance spawned is touched.
            helper.CloseStandardInput();
            if (!helper.WaitForExit(ExitGrace))
                helper.Kill();
        }
        catch
        {
            // teardown is best-effort
        }
        finally
        {
            helper.Dispose();
        }
    }

    // Guards are inlined (not a shared helper) so the platform-compat
    // analyzer recognizes them at the annotated constructor calls.
    private static ICameraExtensionActivator CreateActivator()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("CmioCamera requires macOS");
        return new NexusCameraExtensionActivator();
    }

    private static ICameraHelperLauncher CreateLauncher()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("CmioCamera requires macOS");
        return new ProcessCameraHelperLauncher();
    }
}
