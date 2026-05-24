namespace Nexus.Service.Peripherals.Capabilities;

public interface IBatteryCapability : IPeripheralCapability
{
    /// <summary>Battery level 0–100, or -1 if unavailable.</summary>
    int GetPercent();

    /// <summary>Whether the device is currently charging.</summary>
    bool IsCharging();
}
