using System;
using System.Globalization;

namespace Qos.Service.Peripherals.QSeries;

public sealed class StubQSeriesProvider : IQSeriesProvider
{
    public bool IsConnected() => false;
    public string GetSerial() => "";
    public string GetFormattedTime()
    {
        var now = DateTime.Now;
        var day = now.Day;
        var suffix = (day % 100) switch
        {
            11 or 12 or 13 => "th",
            _ => (day % 10) switch
            {
                1 => "st",
                2 => "nd",
                3 => "rd",
                _ => "th",
            }
        };
        return $"{now:HH:mm:ss} {day}{suffix} {now.ToString("MMMM", CultureInfo.InvariantCulture)}";
    }
}
