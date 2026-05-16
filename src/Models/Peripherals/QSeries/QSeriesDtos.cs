namespace Qos.Service.Models.Peripherals.QSeries;

public class GetSerialNumberResponse : ApiResponse
{
    public string Name { get; set; } = "Q-series";
    public string Serial { get; set; } = "";
}

public class GetQSeriesTimeResponse
{
    public string Time { get; set; } = "";
}
