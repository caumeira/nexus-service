using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Fps;

public interface IFpsProvider : IDisposable
{
    void Start();
    void Stop();
    HardwareComponent GetComponent();
}
