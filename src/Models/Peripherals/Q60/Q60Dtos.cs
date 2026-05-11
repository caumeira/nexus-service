namespace Qos.Service.Models.Peripherals.Q60;

public class GetSerialNumberResponse : ApiResponse
{
    public string Name { get; set; } = "Q60";
    public string Serial { get; set; } = "";
}

public class GetQ60TimeResponse
{
    public string Time { get; set; } = "";
}
