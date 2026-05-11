namespace Qos.Service.Peripherals.Q60;

public interface IQ60Provider
{
    bool IsConnected();
    string GetSerial();
    string GetFormattedTime();
}
