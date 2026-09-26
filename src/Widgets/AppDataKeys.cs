namespace Nexus.Service.Widgets;

/// <summary>
/// App-data key validation: <c>^[a-z0-9][a-z0-9._-]{0,63}$</c>, hand-written
/// (not <see cref="System.Text.RegularExpressions.Regex"/>) to match
/// <see cref="AppIds"/>'s AOT-safe style - the key becomes a filename segment.
/// </summary>
public static class AppDataKeys
{
    public const int MaxLength = 64;

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
        return true;
    }

    private static bool IsLowerOrDigit(char c) => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
}
