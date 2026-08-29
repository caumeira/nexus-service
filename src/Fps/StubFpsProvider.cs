using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Fps;

public sealed class StubFpsProvider : IFpsProvider
{
    public void SetDemand(string source, bool wanted) { }
    public void Dispose() { }

    public bool TryReadCurrentFps(out double fps)
    {
        fps = 0;
        return false;
    }

    public HardwareComponent GetComponent() => new()
    {
        Id = "fps",
        Name = "FPS",
        Sensors = new List<HardwareSensor>(),
    };
}
