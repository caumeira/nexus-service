using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Models.Activity;
using Nexus.Service.Serialization;

namespace Nexus.Service.Activity;

/// <summary>
/// Service-side media provider. GSMTC enumeration runs in the user-session
/// helper (Session 0 cannot see GlobalSystemMediaTransportControls). The
/// helper pushes <c>media.snapshot</c> envelopes on session changes; we
/// cache the latest and serve it to <see cref="GetSessions"/>. Control()
/// and GetAlbumArt() flip back into the helper via
/// <see cref="MediaCommands"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsMediaProvider : IMediaProvider, IDisposable
{
    private static readonly TimeSpan AlbumArtCacheTtl = TimeSpan.FromSeconds(30);

    private readonly HelperRegistry _helper;
    private readonly object _lock = new();
    private Dictionary<string, MediaSession> _snapshot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedArt> _artCache = new(StringComparer.OrdinalIgnoreCase);

    public WindowsMediaProvider(HelperRegistry helper)
    {
        _helper = helper;
        _helper.InboundEnvelope += OnEnvelope;
    }

    private void OnEnvelope(HelperConnection _, HelperEnvelope env)
    {
        if (env.Type != "media.snapshot" || env.Payload is null) return;
        try
        {
            var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.MediaSnapshotPayload);
            if (p is null) return;
            var fresh = new Dictionary<string, MediaSession>(p.Sessions, StringComparer.OrdinalIgnoreCase);
            lock (_lock) { _snapshot = fresh; }
            // Track changes invalidate album-art for the affected session.
            InvalidateStaleArt(fresh);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[media] snapshot decode failed: {ex.Message}");
        }
    }

    private void InvalidateStaleArt(Dictionary<string, MediaSession> fresh)
    {
        foreach (var kv in _artCache)
        {
            if (!fresh.TryGetValue(kv.Key, out var s)) { _artCache.TryRemove(kv.Key, out _); continue; }
            var nowKey = ArtKey(s);
            if (kv.Value.TrackKey != nowKey) _artCache.TryRemove(kv.Key, out _);
        }
    }

    private static string ArtKey(MediaSession s)
        => $"{s.Song?.Title}|{s.Song?.Artist}|{s.Song?.Album}";

    public IReadOnlyDictionary<string, MediaSession> GetSessions()
    {
        lock (_lock) { return _snapshot; }
    }

    public void Control(string source, string action)
    {
        _ = MediaCommands.ControlAsync(_helper, source, action);
    }

    public void Seek(string source, long positionMs)
    {
        _ = MediaCommands.SeekAsync(_helper, source, positionMs);
    }

    public byte[] GetAlbumArt(string source)
    {
        MediaSession? session;
        lock (_lock) { _snapshot.TryGetValue(source, out session); }
        var key = session is null ? "" : ArtKey(session);

        if (_artCache.TryGetValue(source, out var cached) && cached.TrackKey == key && cached.IsFresh)
        {
            return cached.Bytes;
        }

        try
        {
            var bytes = MediaCommands.GetAlbumArtAsync(_helper, source).GetAwaiter().GetResult();
            _artCache[source] = new CachedArt(bytes, key, DateTime.UtcNow);
            return bytes;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[media] album art fetch failed: {ex.Message}");
            return Array.Empty<byte>();
        }
    }

    public void Dispose()
    {
        _helper.InboundEnvelope -= OnEnvelope;
    }

    private readonly record struct CachedArt(byte[] Bytes, string TrackKey, DateTime At)
    {
        public bool IsFresh => DateTime.UtcNow - At < AlbumArtCacheTtl;
    }
}
