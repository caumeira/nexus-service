using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Qos.Service.Models.Activity;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WindowsMediaController;

namespace Qos.Service.Activity;

/// <summary>
/// Windows media provider backed by GlobalSystemMediaTransportControls
/// (GSMTC) via the Dubya.WindowsMediaController NuGet wrapper. Same approach
/// the reference Windows control service uses - matches what Spotify, Edge,
/// Chrome, browser-hosted YouTube, Groove, etc. publish to the OS Now Playing
/// API, so the widget catches every system media session universally (the
/// way iOS/macOS Now Playing does).
///
/// The PowerShell shell-out version this replaces returned empty `{}` from
/// the elevated service even when sessions existed, because GSMTC enumeration
/// from a service-spawned PS host is unreliable. Going through the WinRT
/// projections directly fixes that.
///
/// Sessions are keyed by their friendly source name (Spotify, Edge, Chrome,
/// Music, ...) rather than the raw SourceAppUserModelId so the UI never has
/// to display the AUMID. A friendly -> session-id lookup lets Control() and
/// GetAlbumArt() route back to the underlying GSMTC session.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsMediaProvider : IMediaProvider
{
    private readonly MediaManager _manager;
    private readonly object _lock = new();
    // friendly key (UI-facing) -> session id (the GSMTC AUMID, used to look
    // up the underlying MediaSession in MediaManager.CurrentMediaSessions).
    private readonly Dictionary<string, string> _friendlyToSessionId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedArt> _artCache = new(StringComparer.OrdinalIgnoreCase);

    public WindowsMediaProvider()
    {
        _manager = new MediaManager();
        try { _manager.Start(); }
        catch (Exception ex) { Console.Error.WriteLine($"[media] MediaManager.Start failed: {ex.Message}"); }
    }

    public IReadOnlyDictionary<string, MediaSession> GetSessions()
    {
        var result = new Dictionary<string, MediaSession>(StringComparer.OrdinalIgnoreCase);
        var freshMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string focusedId = string.Empty;
            try { focusedId = _manager.GetFocusedSession()?.Id ?? string.Empty; }
            catch { /* GetFocusedSession can throw when no foreground app exposes media */ }

            foreach (var pair in _manager.CurrentMediaSessions)
            {
                var session = pair.Value;
                if (session?.ControlSession is null) continue;

                GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
                try { props = session.ControlSession.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult(); }
                catch { /* property fetch can fail mid-track-change; skip this poll */ }
                if (props is null) continue;

                var playback = session.ControlSession.GetPlaybackInfo();
                var timeline = session.ControlSession.GetTimelineProperties();

                var friendly = MediaSourceNames.Friendly(session.Id);
                var key = UniqueKey(result, friendly);
                freshMap[key] = session.Id;

                result[key] = new MediaSession
                {
                    SourceAppName = key,
                    IsFocused = !string.IsNullOrEmpty(focusedId) && string.Equals(focusedId, session.Id, StringComparison.Ordinal),
                    Song = new MediaSong
                    {
                        Title = props.Title ?? string.Empty,
                        Artist = props.Artist ?? string.Empty,
                        Album = props.AlbumTitle ?? string.Empty,
                    },
                    Playback = new MediaPlayback
                    {
                        Playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        Stopped = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped,
                        Shuffled = playback.IsShuffleActive == true,
                        RepeatMode = (playback.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None).ToString(),
                        PositionMs = timeline.Position.TotalMilliseconds,
                        DurationMs = timeline.EndTime.TotalMilliseconds,
                    },
                    Controls = new MediaControls
                    {
                        IsPrevEnabled = playback.Controls.IsPreviousEnabled,
                        IsNextEnabled = playback.Controls.IsNextEnabled,
                        IsShuffleEnabled = playback.Controls.IsShuffleEnabled,
                        IsRepeatModeEnabled = playback.Controls.IsRepeatEnabled,
                        IsPlayEnabled = playback.Controls.IsPlayEnabled,
                        IsPauseEnabled = playback.Controls.IsPauseEnabled,
                        IsSeekEnabled = playback.Controls.IsPlaybackPositionEnabled,
                    },
                };
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[media] enumerate failed: {ex.Message}");
        }

        lock (_lock)
        {
            _friendlyToSessionId.Clear();
            foreach (var kv in freshMap) _friendlyToSessionId[kv.Key] = kv.Value;
        }
        return result;
    }

    public void Control(string source, string action)
    {
        if (!TryGetSession(source, out var session)) return;
        var ctrl = session.ControlSession;
        var controls = ctrl.GetPlaybackInfo().Controls;

        try
        {
            switch (action.ToLowerInvariant())
            {
                case "play":
                    if (controls.IsPlayEnabled) ctrl.TryPlayAsync().AsTask().GetAwaiter().GetResult();
                    break;
                case "pause":
                    if (controls.IsPauseEnabled) ctrl.TryPauseAsync().AsTask().GetAwaiter().GetResult();
                    break;
                case "next":
                    if (controls.IsNextEnabled) ctrl.TrySkipNextAsync().AsTask().GetAwaiter().GetResult();
                    break;
                case "prev":
                case "previous":
                    if (controls.IsPreviousEnabled) ctrl.TrySkipPreviousAsync().AsTask().GetAwaiter().GetResult();
                    break;
                case "toggle":
                case "playpause":
                    ctrl.TryTogglePlayPauseAsync().AsTask().GetAwaiter().GetResult();
                    break;
                case "shuffle":
                    if (controls.IsShuffleEnabled)
                    {
                        var current = ctrl.GetPlaybackInfo().IsShuffleActive ?? false;
                        ctrl.TryChangeShuffleActiveAsync(!current).AsTask().GetAwaiter().GetResult();
                    }
                    break;
                case "repeatmode":
                    if (controls.IsRepeatEnabled)
                    {
                        var mode = ctrl.GetPlaybackInfo().AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None;
                        var nextMode = mode switch
                        {
                            MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.List,
                            MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
                            _ => MediaPlaybackAutoRepeatMode.None,
                        };
                        ctrl.TryChangeAutoRepeatModeAsync(nextMode).AsTask().GetAwaiter().GetResult();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[media] control {action} failed: {ex.Message}");
        }
    }

    public byte[] GetAlbumArt(string source)
    {
        if (!TryGetSession(source, out var session)) return Array.Empty<byte>();

        try
        {
            var props = session.ControlSession.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
            if (props is null)
            {
                return Array.Empty<byte>();
            }

            PruneExpiredArtCache();

            var cacheKey = MediaArtworkCacheKey.Build(
                source,
                session.Id,
                props.Title,
                props.Artist,
                props.AlbumTitle);

            if (_artCache.TryGetValue(cacheKey, out var cached) && cached.IsFresh)
            {
                return cached.Bytes;
            }

            if (props.Thumbnail is null)
            {
                _artCache[cacheKey] = new CachedArt(Array.Empty<byte>(), DateTime.UtcNow);
                return Array.Empty<byte>();
            }

            var bytes = StreamRefToBytes(props.Thumbnail).GetAwaiter().GetResult();
            _artCache[cacheKey] = new CachedArt(bytes, DateTime.UtcNow);
            return bytes;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[media] album art failed: {ex.Message}");
            return Array.Empty<byte>();
        }
    }

    private bool TryGetSession(string friendly, out MediaManager.MediaSession session)
    {
        string? sessionId;
        lock (_lock) { _friendlyToSessionId.TryGetValue(friendly, out sessionId); }
        if (sessionId is not null && _manager.CurrentMediaSessions.TryGetValue(sessionId, out var s) && s is not null)
        {
            session = s;
            return true;
        }
        session = null!;
        return false;
    }

    private void PruneExpiredArtCache()
    {
        foreach (var pair in _artCache)
        {
            if (!pair.Value.IsFresh)
            {
                _artCache.TryRemove(pair.Key, out _);
            }
        }
    }

    private static async Task<byte[]> StreamRefToBytes(IRandomAccessStreamReference reference)
    {
        using var stream = await reference.OpenReadAsync();
        var size = (uint)stream.Size;
        if (size == 0) return Array.Empty<byte>();
        var buffer = new Windows.Storage.Streams.Buffer(size);
        await stream.ReadAsync(buffer, size, InputStreamOptions.None);
        return buffer.ToArray();
    }

    private static string UniqueKey(Dictionary<string, MediaSession> existing, string friendly)
    {
        if (!existing.ContainsKey(friendly)) return friendly;
        for (var i = 2; ; i++)
        {
            var candidate = $"{friendly} {i}";
            if (!existing.ContainsKey(candidate)) return candidate;
        }
    }

    private readonly record struct CachedArt(byte[] Bytes, DateTime At)
    {
        public bool IsFresh => (DateTime.UtcNow - At).TotalSeconds < 30;
    }
}
