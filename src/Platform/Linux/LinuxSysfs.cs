using System.Globalization;
using System.IO;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Shared sysfs/file helpers for the Linux providers (serial discovery, hwmon
/// fan control, backlight brightness). Centralizes the read/parse/write
/// boilerplate so each provider doesn't re-roll it. Every method swallows IO
/// errors — a sysfs node can vanish or be permission-gated mid-access — and
/// returns null / false rather than throwing.
/// </summary>
internal static class LinuxSysfs
{
    /// <summary>Read a sysfs attribute as trimmed text, or null if absent/unreadable.</summary>
    internal static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    /// <summary>Read a decimal integer attribute (e.g. pwmN, fanN_input, brightness), or null.</summary>
    internal static int? ReadInt(string path)
        => int.TryParse(ReadText(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Read a hex attribute written without a 0x prefix (e.g. idVendor=3402), or null.</summary>
    internal static int? ReadHex(string path)
        => int.TryParse(ReadText(path), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Write text to a sysfs attribute; returns false on any error (e.g. EACCES).</summary>
    internal static bool WriteText(string path, string value)
    {
        try { File.WriteAllText(path, value); return true; }
        catch { return false; }
    }
}
