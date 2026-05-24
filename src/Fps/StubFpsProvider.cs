using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Fps;

public sealed class StubFpsProvider : IFpsProvider
{
    public void Start() { }
    public void Stop() { }
    public void Dispose() { }

    public HardwareComponent GetComponent() => new()
    {
        Id = "fps",
        Name = "FPS",
        Sensors = new List<HardwareSensor>(),
    };
}
