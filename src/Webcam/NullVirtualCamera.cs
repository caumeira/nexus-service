using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Webcam;

/// <summary>
/// Placeholder camera for platforms whose native backend hasn't landed yet
/// (Windows MediaFoundation / macOS extension are later phases). Accepts any
/// codec passthrough and counts frames so the transport and session logic stay
/// exercisable end-to-end.
/// </summary>
public sealed class NullVirtualCamera : IVirtualCamera
{
    private long _framesWritten;

    public string Name => WebcamDefaults.CameraName;

    public bool Started { get; private set; }

    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    public WebcamFrameInfo LastFrame { get; private set; }

    public WebcamCodecSupport GetCodecSupport(WebcamCodec codec) => WebcamCodecSupport.Passthrough;

    public Task StartAsync(WebcamFormat format, CancellationToken cancellationToken)
    {
        Started = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Started = false;
        return Task.CompletedTask;
    }

    public ValueTask WriteFrameAsync(ReadOnlyMemory<byte> payload, in WebcamFrameInfo info)
    {
        if (Started)
        {
            Interlocked.Increment(ref _framesWritten);
            LastFrame = info;
        }
        return ValueTask.CompletedTask;
    }
}
