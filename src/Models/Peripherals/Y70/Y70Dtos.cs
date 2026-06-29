namespace Nexus.Service.Models.Peripherals.Y70;

public class Y70RotationParams : ApiResponse
{
    /// <summary>One of: Landscape, Portrait, LandscapeFlipped, PortraitFlipped.
    /// Defaults to PortraitFlipped: the Y70 panel is a fixed portrait strip, so
    /// an unset value must not drive it landscape.</summary>
    public string Orientation { get; set; } = "PortraitFlipped";
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
