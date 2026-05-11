using System;

namespace Qos.Service.Activity;

/// <summary>
/// Maps a Windows GSMTC SourceAppUserModelId to a user-friendly label
/// (e.g. "Spotify.Spotify_zpdnekdrzrea0!Spotify" -> "Spotify",
///       "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" -> "Music",
///       "msedge.exe" -> "Edge"). The friendly label is used as both the
/// dictionary key and the displayed source so the UI never shows raw AUMIDs,
/// matching the macOS provider that already keys by "Spotify"/"Music".
/// </summary>
public static class MediaSourceNames
{
    public static string Friendly(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
            return "Media";

        var lower = aumid.ToLowerInvariant();

        if (lower.StartsWith("spotify", StringComparison.Ordinal)) return "Spotify";
        if (lower.Contains("zunemusic", StringComparison.Ordinal)) return "Music";
        if (lower.Contains("zunevideo", StringComparison.Ordinal)) return "Movies & TV";
        if (lower.Contains("media.player", StringComparison.Ordinal)) return "Media Player";
        if (lower.Contains("microsoftedge", StringComparison.Ordinal) || lower.StartsWith("msedge", StringComparison.Ordinal)) return "Edge";
        if (lower.StartsWith("chrome", StringComparison.Ordinal) || lower.Contains("google.chrome", StringComparison.Ordinal)) return "Chrome";
        if (lower.StartsWith("firefox", StringComparison.Ordinal)) return "Firefox";
        if (lower.StartsWith("brave", StringComparison.Ordinal)) return "Brave";
        if (lower.StartsWith("opera", StringComparison.Ordinal)) return "Opera";
        if (lower.StartsWith("vivaldi", StringComparison.Ordinal)) return "Vivaldi";
        if (lower.StartsWith("vlc", StringComparison.Ordinal)) return "VLC";
        if (lower.Contains("itunes", StringComparison.Ordinal)) return "iTunes";
        if (lower.Contains("foobar2000", StringComparison.Ordinal)) return "foobar2000";
        if (lower.Contains("audacious", StringComparison.Ordinal)) return "Audacious";

        var trimmed = aumid;
        var bang = trimmed.IndexOf('!');
        if (bang > 0) trimmed = trimmed[..bang];

        var lastDot = trimmed.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < trimmed.Length - 1) trimmed = trimmed[(lastDot + 1)..];

        var underscore = trimmed.IndexOf('_');
        if (underscore > 0) trimmed = trimmed[..underscore];

        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];

        if (trimmed.Length == 0) return "Media";
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }
}
