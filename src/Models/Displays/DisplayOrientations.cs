namespace Nexus.Service.Models.Displays;

/// <summary>
/// Canonical display-orientation strings (Windows display-orientation set),
/// shared by the Y70 rotation path, the topology provider, and the
/// /displays/{id}/rotation endpoint. DMDO values are the Win32
/// DEVMODE.dmDisplayOrientation encoding.
/// </summary>
public static class DisplayOrientations
{
    public const string Landscape = "Landscape";
    public const string Portrait = "Portrait";
    public const string LandscapeFlipped = "LandscapeFlipped";
    public const string PortraitFlipped = "PortraitFlipped";

    public static bool IsValid(string value)
        => value is Landscape or Portrait or LandscapeFlipped or PortraitFlipped;

    /// <summary>DEVMODE.dmDisplayOrientation (0..3) to the canonical string; "" when out of range.</summary>
    public static string FromDmdo(uint dmdo) => dmdo switch
    {
        0 => Landscape,
        1 => Portrait,
        2 => LandscapeFlipped,
        3 => PortraitFlipped,
        _ => "",
    };

    /// <summary>Canonical string to the macOS rotation in degrees (CGDisplayRotation); null when invalid.</summary>
    public static int? ToMacDegrees(string orientation) => orientation switch
    {
        Landscape => 0,
        Portrait => 90,
        LandscapeFlipped => 180,
        PortraitFlipped => 270,
        _ => null,
    };

    /// <summary>CGDisplayRotation degrees to the canonical string; "" for a non-quadrant angle.</summary>
    public static string FromMacDegrees(double degrees)
    {
        var normalized = ((int)System.Math.Round(degrees) % 360 + 360) % 360;
        return normalized switch
        {
            0 => Landscape,
            90 => Portrait,
            180 => LandscapeFlipped,
            270 => PortraitFlipped,
            _ => "",
        };
    }
}
