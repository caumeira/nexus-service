namespace Qos.Service.Lighting.Capture;

/// <summary>
/// Source of canvas-resolution screen-mirror frames. Two impls today:
/// the cross-platform fallback (uses ffmpeg gdigrab on Windows / avfoundation
/// on macOS) lives inline in <see cref="Effects.ScreenMirrorEffect"/>; the
/// helper-backed impl <see cref="HelperScreenFrameSource"/> is used when the
/// service runs as LocalSystem and cannot reach the desktop directly.
///
/// <see cref="Start"/> tells the source which monitor + canvas size to feed.
/// <see cref="TryAcquireFrame"/> hands the consumer the latest cached frame
/// (or null if none yet). <see cref="Stop"/> tears down capture resources.
/// </summary>
public interface IScreenFrameSource
{
    void Start(string monitorId, int width, int height);
    void Stop();
    /// <summary>Returns the latest captured frame as flat RGB24 or null if none yet. Width/Height match what was passed to Start.</summary>
    byte[]? TryAcquireFrame(out int width, out int height);
}
