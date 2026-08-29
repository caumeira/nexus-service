using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Fps;

public interface IFpsProvider : IDisposable
{
    /// <summary>Registers or clears one named source's demand for capture. Capture
    /// runs while any source wants it; call with the same source id every tick
    /// (idempotent) so ownership never lapses between two independent callers.</summary>
    void SetDemand(string source, bool wanted);
    HardwareComponent GetComponent();

    /// <summary>The instantaneous fps from the same rolling-window calculator
    /// GetComponent()'s fps/current sensor reads - accurate because it only
    /// needs inter-present spacing, unlike counting presents into wall-clock
    /// seconds (DxgKrnl delivers presents in delayed/batched ETW flushes, so
    /// a wall-clock bucket can lose frames to a later batch permanently).
    /// False when capture is not running, no target pid is set, or the last
    /// present is older than the staleness window; fps is 0 in that case.</summary>
    bool TryReadCurrentFps(out double fps);
}
