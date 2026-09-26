using System;
using System.Collections.Generic;

namespace Nexus.Service.Widgets;

/// <summary>
/// App-data key validation: <c>^[a-z0-9][a-z0-9._-]{0,63}$</c>, hand-written
/// (not <see cref="System.Text.RegularExpressions.Regex"/>) to match
/// <see cref="AppIds"/>'s AOT-safe style - the key becomes a filename segment.
/// Also rejects a Windows reserved device name (its portion before the first
/// '.'), case-insensitively, with or without an extension - "con", "con.bak"
/// and "COM1" are all a real device on Windows regardless of what follows the
/// dot, and the key becomes a bare filename with no extension of its own.
/// </summary>
public static class AppDataKeys
{
    public const int MaxLength = 64;

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    public static bool IsValid(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (key.Length > MaxLength) return false;
        if (!IsLowerOrDigit(key[0])) return false;
        for (var i = 1; i < key.Length; i++)
        {
            var c = key[i];
            if (!IsLowerOrDigit(c) && c != '.' && c != '_' && c != '-') return false;
        }
        return !IsReservedWindowsName(key);
    }

    private static bool IsReservedWindowsName(string key)
    {
        var dot = key.IndexOf('.');
        var stem = dot < 0 ? key : key[..dot];
        return ReservedWindowsNames.Contains(stem);
    }

    private static bool IsLowerOrDigit(char c) => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
}
