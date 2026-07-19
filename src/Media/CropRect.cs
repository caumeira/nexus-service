using System.Globalization;
using System.Text;

namespace Nexus.Service.Media;

/// <summary>
/// Normalized crop rectangle (0..1 of the source W/H) plus an optional
/// orientation (horizontal mirror + clockwise rotation) chosen in the cropper.
/// Shared by the panel-background and lighting importers. ffmpeg applies the
/// orientation first, then the crop, so the crop's input-dimension vars (iw/ih)
/// resolve against the oriented frame: crop=iw*W:ih*H:iw*X:ih*Y. That is the
/// same frame the cropper normalized W/H/X/Y against, so preview matches output.
/// </summary>
public readonly record struct CropRect(double X, double Y, double W, double H, int Rotate = 0, bool Mirror = false)
{
    /// <summary>
    /// Parses "x,y,w,h" (legacy, no orientation) or "x,y,w,h,rotate,mirror"
    /// where rotate is 0/90/180/270 (CW degrees) and mirror is 0/1.
    /// </summary>
    public static bool TryParse(string? raw, out CropRect crop)
    {
        crop = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        var parts = raw.Split(',');
        if (parts.Length != 4 && parts.Length != 6)
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
        // TryParse accepts NaN/Infinity, which would survive into the ffmpeg filter.
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h))
        {
            return false;
        }
        if (w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > 1.0001 || y + h > 1.0001)
        {
            return false;
        }

        int rotate = 0;
        bool mirror = false;
        if (parts.Length == 6 && !TryParseOrientation(parts[4], parts[5], out rotate, out mirror))
        {
            return false;
        }
        crop = new CropRect(x, y, w, h, rotate, mirror);
        return true;
    }

    /// <summary>
    /// Parses the trailing orientation fields shared by every crop wire form:
    /// rotate (0/90/180/270 CW degrees) and mirror (0/1). Rejects anything else
    /// so a malformed field falls back to no-orientation rather than a bad filter.
    /// </summary>
    public static bool TryParseOrientation(string rotateRaw, string mirrorRaw, out int rotate, out bool mirror)
    {
        rotate = 0;
        mirror = false;
        if (!int.TryParse(rotateRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out rotate))
        {
            return false;
        }
        rotate = ((rotate % 360) + 360) % 360;
        if (rotate is not (0 or 90 or 180 or 270))
        {
            return false;
        }
        var m = mirrorRaw.Trim();
        if (m is "1")
        {
            mirror = true;
        }
        else if (m is not "0")
        {
            return false;
        }
        return true;
    }

    /// <summary>No crop and no orientation: the source passes through untouched.</summary>
    public bool IsIdentity => X <= 0 && Y <= 0 && W >= 1 && H >= 1 && Rotate == 0 && !Mirror;

    public string ToFfmpegCrop() =>
        $"{OrientationFilter(Rotate, Mirror)}crop=iw*{F(W)}:ih*{F(H)}:iw*{F(X)}:ih*{F(Y)}";

    /// <summary>
    /// ffmpeg filter prefix (trailing comma when non-empty) that mirrors then
    /// rotates clockwise, matching the cropper preview (CSS
    /// rotate(deg) scaleX(-1) applies the mirror in source space, then rotates).
    /// transpose=1 is 90 CW, transpose=2 is 90 CCW (=270 CW).
    /// </summary>
    public static string OrientationFilter(int rotate, bool mirror)
    {
        var sb = new StringBuilder();
        if (mirror)
        {
            sb.Append("hflip,");
        }
        switch (((rotate % 360) + 360) % 360)
        {
            case 90:
                sb.Append("transpose=1,");
                break;
            case 180:
                sb.Append("transpose=1,transpose=1,");
                break;
            case 270:
                sb.Append("transpose=2,");
                break;
        }
        return sb.ToString();
    }

    /// <summary>Filesystem-safe key (ten-thousandths) for the derivative cache.</summary>
    public string CacheKey() => $"{R(X)}-{R(Y)}-{R(W)}-{R(H)}";

    private static int R(double v) => (int)System.Math.Round(v * 10000);
    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
