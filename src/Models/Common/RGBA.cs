namespace Nexus.Service.Models.Common;

/// <summary>
/// Color used in firmware lighting + animations. Bytes for RGB, double for A (0..1).
/// JSON shape: lowercase r/g/b/a.
/// </summary>
public struct RGBA
{
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public double A { get; set; }
}
