namespace Qos.Service.Models.Peripherals.Y70;

public class Y70StatusResponse : ApiResponse
{
    public bool IsConnected { get; set; }
}

public class Y70RotationParams : ApiResponse
{
    /// <summary>One of: Landscape, Portrait, LandscapeFlipped, PortraitFlipped.</summary>
    public string Orientation { get; set; } = "Landscape";
}

public class Y70BrightnessResponse : ApiResponse
{
    public int Brightness { get; set; }
}

public class Y70BrightnessParams { public int Brightness { get; set; } }

public class Y70ToggleScreenResponse : ApiResponse
{
    public bool Toggle { get; set; }
}

public class Y70ToggleScreenParams { public bool Toggle { get; set; } }

public class Y70IsRotatedResponse : ApiResponse
{
    public bool IsRotated { get; set; }
}
