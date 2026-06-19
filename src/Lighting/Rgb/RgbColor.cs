namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Single RGB triplet - packed 3 bytes, no padding. The wire format adds a
/// fourth padding byte per LED; the in-memory representation omits it.
/// </summary>
public readonly struct RgbColor
{
    public readonly byte R;
    public readonly byte G;
    public readonly byte B;

    public RgbColor(byte r, byte g, byte b) { R = r; G = g; B = b; }

    public static readonly RgbColor Black = new(0, 0, 0);

    /// <summary>Scale all channels by <paramref name="brightness"/> in [0..1].</summary>
    public RgbColor Scale(double brightness)
    {
        if (brightness <= 0)
        {
            return Black;
        }

        if (brightness >= 1)
        {
            return this;
        }

        return new RgbColor(
            (byte)(R * brightness),
            (byte)(G * brightness),
            (byte)(B * brightness));
    }
}
