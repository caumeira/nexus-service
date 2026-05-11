using Qos.Service.Models.Sensors;

namespace Qos.Service.Fps;

public interface IFpsProvider : IDisposable
{
    void Start();
    void Stop();
    HardwareComponent GetComponent();
}
