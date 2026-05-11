using System.Collections.Generic;

namespace Qos.Service.Lighting.Rgb;

/// <summary>
/// One RGB device discovered by the OpenRGB SDK server. Mirrors the subset of
/// fields we actually need from the OpenRGB controller struct.
/// </summary>
public sealed class RgbDevice
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public uint Type { get; set; }
    public int LedCount { get; set; }
    public string Vendor { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Location { get; set; } = "";
    public List<string> LedNames { get; set; } = new();
    public List<RgbZone> Zones { get; set; } = new();

    private string? _stableId;

    /// <summary>
    /// Stable identifier derived from serial or location, falling back to
    /// index when neither is available. Cached after first access.
    /// </summary>
    public string StableId => _stableId ??= BuildStableId();

    private string BuildStableId()
    {
        if (!string.IsNullOrEmpty(Serial))
            return $"openrgb-s-{Sanitize(Serial)}";
        if (!string.IsNullOrEmpty(Location))
            return $"openrgb-l-{Sanitize(Location)}";
        return $"openrgb-{Index}";
    }

    private static string Sanitize(string s)
    {
        var buf = new char[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            buf[i] = char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_';
        }
        return new string(buf);
    }
}

public sealed class RgbZone
{
    public string Name { get; set; } = "";
    /// <summary>OpenRGB zone type: 0=Single, 1=Linear, 2=Matrix.</summary>
    public uint ZoneType { get; set; }
    public int LedCount { get; set; }
    /// <summary>Matrix width in columns, or 0 when the zone is a linear strip.</summary>
    public int MatrixWidth { get; set; }
    /// <summary>Matrix height in rows, or 0 when the zone is a linear strip.</summary>
    public int MatrixHeight { get; set; }
    /// <summary>Row-major grid of zone-local LED indices. -1 means the cell is unpopulated (gap in the key layout). null when no matrix was provided.</summary>
    public int[]? MatrixMap { get; set; }
}
