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
}
