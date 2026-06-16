using System.Globalization;

namespace Nexus.Service.Media;

/// <summary>
/// Normalized crop rectangle (0..1 of the source W/H), shared by the panel
/// background and lighting importers. Applied via ffmpeg's input-dimension vars
/// so the source pixel size is never needed: crop=iw*W:ih*H:iw*X:ih*Y.
/// </summary>
public readonly record struct CropRect(double X, double Y, double W, double H)
{
    public static bool TryParse(string? raw, out CropRect crop)
    {
        crop = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        var parts = raw.Split(',');
        if (parts.Length != 4)
        {
            return false;
        }
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
        {
            return false;
        }
        if (w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > 1.0001 || y + h > 1.0001)
        {
            return false;
        }
        crop = new CropRect(x, y, w, h);
        return true;
    }

    public string ToFfmpegCrop() => $"crop=iw*{F(W)}:ih*{F(H)}:iw*{F(X)}:ih*{F(Y)}";

    /// <summary>Filesystem-safe key (ten-thousandths) for the derivative cache.</summary>
    public string CacheKey() => $"{R(X)}-{R(Y)}-{R(W)}-{R(H)}";

    private static int R(double v) => (int)System.Math.Round(v * 10000);
    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
