using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Qos.Service.Models.Activity;
using Qos.Service.Platform;

namespace Qos.Service.Activity;

/// <summary>
/// Real macOS media provider via AppleScript. Queries Spotify and Music.app
/// (Apple Music) for now-playing info, playback state, and control actions.
///
/// Each GetSessions() call does 2 AppleScript invocations (~100ms each) — one
/// per player app. If a player isn't running, osascript returns immediately
/// with no error (the script guards with `application X is running`).
///
/// Control commands (play/pause/next/prev) are sent via AppleScript `tell`.
///
/// Album art: Spotify exposes `artwork url` via AppleScript; Music.app exposes
/// `raw data of artwork 1` but extracting that to PNG is non-trivial. For
/// Spotify we fetch the URL and download; for Music we return empty.
/// </summary>
public sealed class MacMediaProvider : IMediaProvider
{
    private static readonly string[] Players = { "Spotify", "Music" };

    public IReadOnlyDictionary<string, MediaSession> GetSessions()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new Dictionary<string, MediaSession>();
        }

        var sessions = new Dictionary<string, MediaSession>();

        foreach (var player in Players)
        {
            var session = QueryPlayer(player);
            if (session is not null)
            {
                sessions[player] = session;
            }
        }

        return sessions;
    }

    public void Control(string source, string action)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return;
        }

        var cmd = action.ToLowerInvariant() switch
        {
            "play" => $"tell application \"{source}\" to play",
            "pause" => $"tell application \"{source}\" to pause",
            "next" => $"tell application \"{source}\" to next track",
            "prev" or "previous" => $"tell application \"{source}\" to previous track",
            "playpause" => $"tell application \"{source}\" to playpause",
            _ => null,
        };

        if (cmd is not null)
        {
            RunOsascript(cmd);
        }
    }

    public byte[] GetAlbumArt(string source)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Array.Empty<byte>();
        }

        if (string.Equals(source, "Spotify", StringComparison.OrdinalIgnoreCase))
        {
            var url = RunOsascript(
                "if application \"Spotify\" is running then\n" +
                "  tell application \"Spotify\" to return artwork url of current track\n" +
                "end if").Trim();

            if (!string.IsNullOrEmpty(url) && url.StartsWith("http"))
            {
                return DownloadBytes(url);
            }
        }

        return Array.Empty<byte>();
    }

    private static MediaSession? QueryPlayer(string player)
    {
        var script = player switch
        {
            "Spotify" => SpotifyScript,
            "Music" => MusicScript,
            _ => null,
        };
        if (script is null)
        {
            return null;
        }

        var result = RunOsascript(script).Trim();
        if (string.IsNullOrEmpty(result) || result == "not_running")
        {
            return null;
        }

        // Output format: "title|||artist|||album|||state|||position|||duration"
        var parts = result.Split("|||");
        if (parts.Length < 4)
        {
            return null;
        }

        var title = parts.Length > 0 ? parts[0] : "";
        var artist = parts.Length > 1 ? parts[1] : "";
        var album = parts.Length > 2 ? parts[2] : "";
        var state = parts.Length > 3 ? parts[3] : "";
        var position = parts.Length > 4 && double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var pos) ? pos : 0;
        var duration = parts.Length > 5 && double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var dur) ? dur : 0;

        var playing = string.Equals(state, "playing", StringComparison.OrdinalIgnoreCase);

        return new MediaSession
        {
            SourceAppName = player,
            IsFocused = false,
            Song = new MediaSong { Title = title, Artist = artist, Album = album },
            Playback = new MediaPlayback
            {
                Playing = playing,
                Stopped = string.Equals(state, "stopped", StringComparison.OrdinalIgnoreCase),
                PositionMs = position * 1000,
                DurationMs = duration * 1000,
            },
            Controls = new MediaControls
            {
                IsPlayEnabled = !playing,
                IsPauseEnabled = playing,
                IsNextEnabled = true,
                IsPrevEnabled = true,
            },
        };
    }

    private const string SpotifyScript =
        "if application \"Spotify\" is running then\n" +
        "  tell application \"Spotify\"\n" +
        "    set t to name of current track\n" +
        "    set a to artist of current track\n" +
        "    set al to album of current track\n" +
        "    set s to player state as string\n" +
        "    set p to player position\n" +
        "    set d to duration of current track / 1000\n" +
        "    return t & \"|||\" & a & \"|||\" & al & \"|||\" & s & \"|||\" & p & \"|||\" & d\n" +
        "  end tell\n" +
        "else\n" +
        "  return \"not_running\"\n" +
        "end if";

    private const string MusicScript =
        "if application \"Music\" is running then\n" +
        "  tell application \"Music\"\n" +
        "    if player state is not stopped then\n" +
        "      set t to name of current track\n" +
        "      set a to artist of current track\n" +
        "      set al to album of current track\n" +
        "      set s to player state as string\n" +
        "      set p to player position\n" +
        "      set d to duration of current track\n" +
        "      return t & \"|||\" & a & \"|||\" & al & \"|||\" & s & \"|||\" & p & \"|||\" & d\n" +
        "    else\n" +
        "      return \"not_running\"\n" +
        "    end if\n" +
        "  end tell\n" +
        "else\n" +
        "  return \"not_running\"\n" +
        "end if";

    private static string RunOsascript(string script)
        => ShellExecutor.Run("/usr/bin/osascript", 3000, "-e", script);

    private static byte[] DownloadBytes(string url)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            return client.GetByteArrayAsync(url).GetAwaiter().GetResult();
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }
}
