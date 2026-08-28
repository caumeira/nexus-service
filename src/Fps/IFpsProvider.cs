using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Fps;

/// <summary>One completed second's frame count for the pid that was being
/// tracked. Frames is 0 when the target had no presents that second (still
/// focused, nothing drawn - loading, paused, minimized), distinct from the
/// second never appearing at all (no target existed then).</summary>
public readonly record struct FpsSecond(long TsSec, int Pid, int Frames);

public interface IFpsProvider : IDisposable
{
    /// <summary>Registers or clears one named source's demand for capture. Capture
    /// runs while any source wants it; call with the same source id every tick
    /// (idempotent) so ownership never lapses between two independent callers.</summary>
    void SetDemand(string source, bool wanted);
    HardwareComponent GetComponent();

    /// <summary>Every completed second with ts &gt; afterTsSec, oldest first.
    /// A second counts as completed once wall-clock time has moved far enough
    /// past it that any in-flight ETW delivery for it must already have
    /// arrived; it never depends on a later present arriving to close it, so
    /// a paused or minimized target still reports its 0-frame seconds. Each
    /// caller tracks its own afterTsSec (the last ts it consumed) rather than
    /// the provider tracking per-caller cursors, so independent 1Hz callers
    /// never race or double-count. Empty when capture is not running.</summary>
    IReadOnlyList<FpsSecond> ReadCompletedSeconds(long afterTsSec);
}
