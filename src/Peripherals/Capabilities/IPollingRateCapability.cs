using System.Collections.Generic;

namespace Qos.Service.Peripherals.Capabilities;

public interface IPollingRateCapability : IPeripheralCapability
{
    /// <summary>Supported polling rates in Hz, typically 125/500/1000/2000/4000/8000.</summary>
    IReadOnlyList<int> SupportedHz { get; }

    int GetCurrentHz();
    bool SetHz(int hz);
}
