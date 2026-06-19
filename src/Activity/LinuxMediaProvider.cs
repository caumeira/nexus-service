using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Nexus.Service.Models.Activity;
using Nexus.Service.Platform.Linux.DBus;

namespace Nexus.Service.Activity;

/// <summary>
/// Linux now-playing provider over MPRIS (org.mpris.MediaPlayer2) on the D-Bus
/// session bus, reusing the process-wide <see cref="DBusConnection"/>. Lists the
/// MPRIS player names, reads each player's Player-interface properties
/// (PlaybackStatus + Metadata + Position + Can* flags) via Properties.GetAll,
/// and maps them onto the shared <see cref="MediaSession"/> shape. Transport
/// control issues Play/Pause/Next/Previous method calls; album art comes from
/// the metadata <c>mpris:artUrl</c> (file:// or http(s)). All D-Bus work is the
/// hand-rolled marshaller - AOT-safe, no native deps.
/// </summary>
public sealed class LinuxMediaProvider : IMediaProvider
{
    private const string MprisPrefix = "org.mpris.MediaPlayer2.";
    private const string MprisPath = "/org/mpris/MediaPlayer2";
    private const string PlayerIface = "org.mpris.MediaPlayer2.Player";
    private const string PropsIface = "org.freedesktop.DBus.Properties";

    private static readonly IReadOnlyDictionary<string, MediaSession> Empty = new Dictionary<string, MediaSession>();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    private readonly DBusConnection _dbus;

    public LinuxMediaProvider(DBusConnection dbus) => _dbus = dbus;

    public IReadOnlyDictionary<string, MediaSession> GetSessions()
    {
        if (!OperatingSystem.IsLinux())
            return Empty;
        try
        {
            return GetSessionsAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[media-linux] GetSessions failed: {ex.Message}");
            return Empty;
        }
    }

    public void Control(string source, string action)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(source))
            return;
        var member = action.ToLowerInvariant() switch
        {
            "play" => "Play",
            "pause" => "Pause",
            "playpause" => "PlayPause",
            "next" => "Next",
            "prev" or "previous" => "Previous",
            _ => null,
        };
        if (member is null)
            return;
        try
        {
            _dbus.StartAsync().GetAwaiter().GetResult();
            _dbus.CallAsync(source, MprisPath, PlayerIface, member, "", null).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[media-linux] Control {action} on {source} failed: {ex.Message}");
        }
    }

    public byte[] GetAlbumArt(string source)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(source))
            return Array.Empty<byte>();
        try
        {
            _dbus.StartAsync().GetAwaiter().GetResult();
            var meta = GetMetadataAsync(source).GetAwaiter().GetResult();
            var url = Str(meta, "mpris:artUrl");
            return FetchArt(url);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[media-linux] GetAlbumArt for {source} failed: {ex.Message}");
            return Array.Empty<byte>();
        }
    }

    private async Task<IReadOnlyDictionary<string, MediaSession>> GetSessionsAsync()
    {
        await _dbus.StartAsync();

        var listReply = await _dbus.CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus",
            "org.freedesktop.DBus", "ListNames", "", null);
        var names = new DBusReader(listReply.Body).ReadStringArray()
            .Where(n => n.StartsWith(MprisPrefix, StringComparison.Ordinal))
            .ToList();

        var sessions = new Dictionary<string, MediaSession>(StringComparer.Ordinal);
        var focusedAssigned = false;
        foreach (var name in names)
        {
            try
            {
                var props = await GetAllPlayerPropsAsync(name);
                var session = BuildSession(name, props, !focusedAssigned);
                if (session.IsFocused)
                    focusedAssigned = true;
                sessions[name] = session;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[media-linux] player {name} skipped: {ex.Message}");
            }
        }
        return sessions;
    }

    private async Task<Dictionary<string, object?>> GetAllPlayerPropsAsync(string busName)
    {
        var reply = await _dbus.CallAsync(busName, MprisPath, PropsIface, "GetAll", "s",
            w => w.WriteString(PlayerIface));
        return new DBusReader(reply.Body).ReadStringVariantDict();
    }

    private async Task<Dictionary<string, object?>> GetMetadataAsync(string busName)
    {
        var reply = await _dbus.CallAsync(busName, MprisPath, PropsIface, "Get", "ss", w =>
        {
            w.WriteString(PlayerIface);
            w.WriteString("Metadata");
        });
        var r = new DBusReader(reply.Body);
        r.ReadSignature(); // the wrapping variant signature ("a{sv}")
        return r.ReadStringVariantDict();
    }

    internal static MediaSession BuildSession(string busName, Dictionary<string, object?> props, bool canFocus)
    {
        var status = Str(props, "PlaybackStatus");
        var playing = status.Equals("Playing", StringComparison.OrdinalIgnoreCase);
        var meta = props.TryGetValue("Metadata", out var m) && m is Dictionary<string, object?> md
            ? md
            : new Dictionary<string, object?>();

        var artist = meta.TryGetValue("xesam:artist", out var a) && a is List<object?> arr
            ? string.Join(", ", arr.OfType<string>())
            : Str(meta, "xesam:artist");

        return new MediaSession
        {
            SourceAppName = FriendlyName(busName),
            IsFocused = canFocus && playing,
            Song = new MediaSong
            {
                Title = Str(meta, "xesam:title"),
                Artist = artist,
                Album = Str(meta, "xesam:album"),
            },
            Playback = new MediaPlayback
            {
                Playing = playing,
                Stopped = status.Equals("Stopped", StringComparison.OrdinalIgnoreCase),
                Shuffled = Bool(props, "Shuffle"),
                RepeatMode = Str(props, "LoopStatus") is { Length: > 0 } loop ? loop : "None",
                PositionMs = Long(props, "Position") / 1000.0,
                DurationMs = Long(meta, "mpris:length") / 1000.0,
            },
            Controls = new MediaControls
            {
                IsNextEnabled = Bool(props, "CanGoNext"),
                IsPrevEnabled = Bool(props, "CanGoPrevious"),
                IsPlayEnabled = Bool(props, "CanPlay"),
                IsPauseEnabled = Bool(props, "CanPause"),
                IsSeekEnabled = Bool(props, "CanSeek"),
            },
        };
    }

    private static byte[] FetchArt(string url)
    {
        if (string.IsNullOrEmpty(url))
            return Array.Empty<byte>();
        try
        {
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var path = new Uri(url).LocalPath;
                return File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
            }
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[media-linux] art fetch failed: {ex.Message}");
        }
        return Array.Empty<byte>();
    }

    /// <summary>"org.mpris.MediaPlayer2.firefox.instance_123" -> "firefox".</summary>
    private static string FriendlyName(string busName)
    {
        var tail = busName.Length > MprisPrefix.Length ? busName[MprisPrefix.Length..] : busName;
        var first = tail.Split('.', 2)[0];
        return first.Length == 0 ? tail : char.ToUpperInvariant(first[0]) + first[1..];
    }

    private static string Str(Dictionary<string, object?> d, string key)
        => d.TryGetValue(key, out var v) && v is string s ? s : "";

    private static bool Bool(Dictionary<string, object?> d, string key)
        => d.TryGetValue(key, out var v) && v is bool b && b;

    private static long Long(Dictionary<string, object?> d, string key)
        => d.TryGetValue(key, out var v) && v is long l ? l : 0;
}
