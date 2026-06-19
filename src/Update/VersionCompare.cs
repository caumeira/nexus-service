using System;

namespace Nexus.Service.Update;

/// <summary>
/// Semver compare for "v{major}.{minor}.{patch}" version strings. A leading
/// "v" or "V" is stripped before parsing. Pre-release suffixes ("-rc1", etc.)
/// are ignored. Non-parseable strings are never newer.
/// </summary>
public static class VersionCompare
{
    /// <summary>
    /// Returns true when <paramref name="candidate"/> is strictly newer than
    /// <paramref name="current"/>. Unparseable strings are never newer.
    /// </summary>
    public static bool IsNewer(string candidate, string current)
    {
        if (!TryParseSemver(candidate, out var c)) return false;
        if (!TryParseSemver(current, out var cur)) return false;
        return c.CompareTo(cur) > 0;
    }

    /// <summary>
    /// Parses "v{major}.{minor}.{patch}" (leading v/V optional, pre-release
    /// suffix ignored) into a comparable tuple. Returns false on garbage input.
    /// </summary>
    public static bool TryParseSemver(string tag, out (int Major, int Minor, int Patch) value)
    {
        value = default;
        if (string.IsNullOrEmpty(tag)) return false;

        var s = tag.AsSpan();
        if (s[0] is 'v' or 'V')
        {
            s = s.Slice(1);
        }

        if (s.IsEmpty) return false;

        // Strip pre-release suffix starting at the first '-'.
        var dash = s.IndexOf('-');
        if (dash == 0) return false;
        if (dash > 0)
        {
            s = s.Slice(0, dash);
        }

        // Require at least one dot so bare integers ("63") are not accepted
        // as semver (those were the old monotonic tag format).
        if (s.IndexOf('.') < 0) return false;

        var parts = s.ToString().Split('.');
        if (parts.Length < 1) return false;

        if (!int.TryParse(parts[0], out var major) || major < 0) return false;
        var minor = 0;
        var patch = 0;
        if (parts.Length >= 2 && (!int.TryParse(parts[1], out minor) || minor < 0)) return false;
        if (parts.Length >= 3 && (!int.TryParse(parts[2], out patch) || patch < 0)) return false;

        value = (major, minor, patch);
        return true;
    }
}
