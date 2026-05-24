namespace Nexus.Service.Peripherals.Capabilities;

public interface ISleepCapability : IPeripheralCapability
{
    /// <summary>Idle timeout in seconds before the device sleeps (0 = disabled where supported).</summary>
    int GetIdleSeconds();
    bool SetIdleSeconds(int seconds);

    /// <summary>Battery percent threshold that triggers a low-battery warning (0 if unsupported).</summary>
    int GetLowBatteryPercent();
    bool SetLowBatteryPercent(int percent);
}
