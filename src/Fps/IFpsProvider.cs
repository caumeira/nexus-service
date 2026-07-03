using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Fps;

public interface IFpsProvider : IDisposable
{
    /// <summary>Registers or clears one named source's demand for capture. Capture
    /// runs while any source wants it; call with the same source id every tick
    /// (idempotent) so ownership never lapses between two independent callers.</summary>
    void SetDemand(string source, bool wanted);
    HardwareComponent GetComponent();
}
