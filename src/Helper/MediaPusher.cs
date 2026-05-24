#if WINDOWS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Models.Activity;
using Nexus.Service.Serialization;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WindowsMediaController;

namespace Nexus.Service.Helper;

/// <summary>
/// Helper-side GSMTC bridge. Owns the <see cref="MediaManager"/> in the
/// user's session (where the GSMTC session manager is actually visible),
/// pushes <c>media.snapshot</c> envelopes to the service when sessions
/// change, and handles <c>media.control</c> / <c>media.getAlbumArt</c>
/// commands coming back the other way.
///
/// Subscribing to MediaManager events instead of polling means a snapshot
/// only goes out when something actually changed - usually a few per
/// minute when the user is actively listening.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class MediaPusher : IDisposable
{
    private readonly HelperOutbound _outbound;
    private readonly MediaManager _manager;
    private readonly object _mapLock = new();
    private readonly Dictionary<string, string> _friendlyToSessionId = new(StringComparer.OrdinalIgnoreCase);
    private int _pushScheduled;

    public MediaPusher(HelperOutbound outbound)
    {
        _outbound = outbound;
        _manager = new MediaManager();
        _manager.OnAnySessionOpened += _ => SchedulePush();
        _manager.OnAnySessionClosed += _ => SchedulePush();
        _manager.OnFocusedSessionChanged += _ => SchedulePush();
        _manager.OnAnyMediaPropertyChanged += (_, __) => SchedulePush();
        _manager.OnAnyPlaybackStateChanged += (_, __) => SchedulePush();
        try { _manager.Start(); }
        catch (Exception ex) { Console.Error.WriteLine($"[media-pusher] MediaManager.Start failed: {ex.Message}"); }
        SchedulePush();
    }

    /// <summary>
    /// Coalesces bursts of GSMTC events (a single track change fires
    /// several) into one outbound push. Atomic CAS gate; the first event
    /// in a burst arms a short delay, subsequent events within the window
    /// are folded into the same push.
    /// </summary>
    private void SchedulePush()
    {
        if (Interlocked.Exchange(ref _pushScheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(150).ConfigureAwait(false);
            Interlocked.Exchange(ref _pushScheduled, 0);
            try { await PushAsync().ConfigureAwait(false); }
            catch (Exception ex) { Console.Error.WriteLine($"[media-pusher] push failed: {ex.Message}"); }
        });
    }

    private async Task PushAsync()
    {
        var sessions = BuildSnapshot();
        var payload = new MediaSnapshotPayload { Sessions = sessions };
        await _outbound.SendAsync(
            "media.snapshot",
            payload,
            AppJsonContext.Default.MediaSnapshotPayload).ConfigureAwait(false);
    }

    private Dictionary<string, MediaSession> BuildSnapshot()
    {
        var result = new Dictionary<string, MediaSession>(StringComparer.OrdinalIgnoreCase);
        var freshMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string focusedId = string.Empty;
            try { focusedId = _manager.GetFocusedSession()?.Id ?? string.Empty; }
            catch { }

            foreach (var pair in _manager.CurrentMediaSessions)
            {
                var session = pair.Value;
                if (session?.ControlSession is null) continue;

                GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
                try { props = session.ControlSession.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult(); }
                catch { }
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
            Console.Error.WriteLine($"[media-pusher] enumerate failed: {ex.Message}");
        }

        lock (_mapLock)
        {
            _friendlyToSessionId.Clear();
            foreach (var kv in freshMap) _friendlyToSessionId[kv.Key] = kv.Value;
        }
        return result;
    }

    /// <summary>
    /// Called by MediaHandler when a `media.control` envelope arrives.
    /// </summary>
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
            Console.Error.WriteLine($"[media-pusher] control {action} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Called by MediaHandler when a `media.getAlbumArt` envelope arrives.
    /// </summary>
    public byte[] GetAlbumArt(string source)
    {
        if (!TryGetSession(source, out var session)) return Array.Empty<byte>();
        try
        {
            var props = session.ControlSession.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
            if (props?.Thumbnail is null) return Array.Empty<byte>();
            return StreamRefToBytes(props.Thumbnail).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[media-pusher] album art failed: {ex.Message}");
            return Array.Empty<byte>();
        }
    }

    private bool TryGetSession(string friendly, out MediaManager.MediaSession session)
    {
        string? sessionId;
        lock (_mapLock) { _friendlyToSessionId.TryGetValue(friendly, out sessionId); }
        if (sessionId is not null && _manager.CurrentMediaSessions.TryGetValue(sessionId, out var s) && s is not null)
        {
            session = s;
            return true;
        }
        session = null!;
        return false;
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

    public void Dispose()
    {
        try { _manager.Dispose(); } catch { }
    }
}
#endif
