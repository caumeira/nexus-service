using Qos.Service.Models.Sensors;

namespace Qos.Service.Fps;

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
