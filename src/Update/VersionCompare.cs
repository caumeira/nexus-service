using System;

namespace Nexus.Service.Update;

/// <summary>
/// Numeric compare for "v{N}" version tags. Any string that does not match the
/// "v" + non-negative integer pattern is treated as not-newer.
/// </summary>
public static class VersionCompare
{
    /// <summary>
    /// Returns true when <paramref name="candidate"/> is strictly newer than
    /// <paramref name="current"/>. Unparseable strings are never newer.
    /// </summary>
    public static bool IsNewer(string candidate, string current)
    {
        if (!TryParse(candidate, out var c)) return false;
        if (!TryParse(current, out var cur)) return false;
        return c > cur;
    }

    /// <summary>Parses "v{N}" to the integer N. Returns false on any other input.</summary>
    public static bool TryParse(string tag, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(tag)) return false;
        if (tag[0] != 'v' && tag[0] != 'V') return false;
        return int.TryParse(tag.AsSpan(1), out value) && value >= 0;
    }
}
