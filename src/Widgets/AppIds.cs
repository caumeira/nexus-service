using System;

namespace Nexus.Service.Widgets;

/// <summary>
/// Widget id validation. Ids are reverse-DNS, ASCII, lowercase, with dots
/// and hyphens. The URL routes use the id as a path segment, so the
/// allow-set must be tight enough that a hostile manifest cannot escape the
/// per-widget directory or smuggle path separators.
/// </summary>
public static class AppIds
{
    public const int MaxLength = 128;

    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        if (id.Length > MaxLength) return false;
        // Must start with a letter and contain at least one dot (reverse-DNS).
        if (!IsLowerLetter(id[0])) return false;
        // Last char must be a letter or digit - dots and dashes only sit
        // between segments.
        var last = id[^1];
        if (!IsLowerLetter(last) && !IsDigit(last)) return false;
        var sawDot = false;
        var prevDot = false;
        for (int i = 0; i < id.Length; i++)
        {
            var c = id[i];
            if (IsLowerLetter(c) || IsDigit(c))
            {
                prevDot = false;
                continue;
            }
            if (c == '.')
            {
                if (prevDot) return false;     // no consecutive dots
                sawDot = true;
                prevDot = true;
                continue;
            }
            if (c == '-')
            {
                if (prevDot) return false;     // no dash directly after a dot
                prevDot = false;
                continue;
            }
            return false;
        }
        return sawDot;
    }

    private static bool IsLowerLetter(char c) => c >= 'a' && c <= 'z';
    private static bool IsDigit(char c) => c >= '0' && c <= '9';
}
