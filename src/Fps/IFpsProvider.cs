using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Fps;

public interface IFpsProvider : IDisposable
{
    /// <summary>Registers or clears one named source's demand for capture. Capture
    /// runs while any source wants it; call with the same source id every tick
    /// (idempotent) so ownership never lapses between two independent callers.</summary>
    void SetDemand(string source, bool wanted);
    HardwareComponent GetComponent();

    /// <summary>Reads the completed frame count for epoch-second tsSec, the
    /// second that just elapsed. A pure query against the last-finalized
    /// second (never consumed), so any number of independent 1Hz callers can
    /// ask for the same tsSec without racing each other. Returns false when
    /// tsSec has not finished yet, capture is not running, or the second
    /// belonged to a different target pid.</summary>
    bool TryReadSecond(long tsSec, out int pid, out int frames);
}
