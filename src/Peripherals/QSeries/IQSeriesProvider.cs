namespace Qos.Service.Peripherals.QSeries;

public interface IQSeriesProvider
{
    bool IsConnected();
    string GetSerial();
    string GetFormattedTime();
}
