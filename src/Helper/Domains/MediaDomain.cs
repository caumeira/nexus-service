#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Activity;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains
{
    /// <summary>
    /// Payload for <c>media.snapshot</c>. Helper-to-service, one-way. The
    /// full current set of GSMTC media sessions (Spotify, Edge, Chrome,
    /// ...). Pushed when any session changes; service caches the latest
    /// snapshot and serves it to the SPA via GetSessions().
    /// </summary>
    public sealed class MediaSnapshotPayload
    {
        public Dictionary<string, MediaSession> Sessions { get; set; } = new();
    }

    /// <summary>
    /// Payload for <c>media.control</c>. Service-to-helper command, expects
    /// a standard ok-result. Source is the friendly session key (e.g.
    /// "Spotify"); Action is play / pause / next / prev / toggle / shuffle /
    /// repeatMode.
    /// </summary>
    public sealed class MediaControlPayload
    {
        public string Source { get; set; } = "";
        public string Action { get; set; } = "";
    }

    /// <summary>
    /// Payload for <c>media.getAlbumArt</c>. Service-to-helper RPC. The
    /// helper replies with an <see cref="AlbumArtResult"/> in
    /// HelperResult.Payload.
    /// </summary>
    public sealed class AlbumArtRequest
    {
        public string Source { get; set; } = "";
    }

    /// <summary>
    /// Payload returned for <c>media.getAlbumArt</c>. Empty Bytes means no
    /// art (Stopped session, missing thumbnail).
    /// </summary>
    public sealed class AlbumArtResult
    {
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
    }

    // JSON source-gen registration lives in src/Serialization/AppJsonContext.cs.

    [SupportedOSPlatform("windows")]
    public static class MediaCommands
    {
        public static Task ControlAsync(HelperRegistry registry, string source, string action, CancellationToken ct = default)
        {
            var conn = registry.GetAny();
            if (conn is null) return Task.CompletedTask;
            return conn.SendAsync(
                type: "media.control",
                payload: new MediaControlPayload { Source = source, Action = action },
                payloadType: AppJsonContext.Default.MediaControlPayload,
                ct: ct);
        }

        public static async Task<byte[]> GetAlbumArtAsync(HelperRegistry registry, string source, CancellationToken ct = default)
        {
            var conn = registry.GetAny();
            if (conn is null) return Array.Empty<byte>();
            var result = await conn.SendCommandAsync(
                type: "media.getAlbumArt",
                payload: new AlbumArtRequest { Source = source },
                payloadType: AppJsonContext.Default.AlbumArtRequest,
                timeoutMs: 4000,
                ct: ct).ConfigureAwait(false);
            if (!result.Ok || result.Payload is null) return Array.Empty<byte>();
            try
            {
                var art = JsonSerializer.Deserialize(result.Payload.Value, AppJsonContext.Default.AlbumArtResult);
                return art?.Bytes ?? Array.Empty<byte>();
            }
            catch { return Array.Empty<byte>(); }
        }
    }

    [SupportedOSPlatform("windows")]
    public sealed class MediaHandler
    {
        private readonly Action<string, string> _control;
        private readonly Func<string, byte[]> _getAlbumArt;

        public MediaHandler(Action<string, string> control, Func<string, byte[]> getAlbumArt)
        {
            _control = control;
            _getAlbumArt = getAlbumArt;
        }

        public void Register(HelperHandlerRegistry registry)
        {
            registry.Register("media.control", (env, _) =>
            {
                if (env.Payload is null) return Task.FromResult(env.Ok());
                var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.MediaControlPayload);
                if (p is not null) _control(p.Source, p.Action);
                return Task.FromResult(env.Ok());
            });
            registry.Register("media.getAlbumArt", (env, _) =>
            {
                var bytes = Array.Empty<byte>();
                if (env.Payload is not null)
                {
                    var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.AlbumArtRequest);
                    if (p is not null) bytes = _getAlbumArt(p.Source);
                }
                return Task.FromResult(new HelperResult
                {
                    Id = env.Id ?? "",
                    Ok = true,
                    Payload = JsonSerializer.SerializeToElement(
                        new AlbumArtResult { Bytes = bytes },
                        AppJsonContext.Default.AlbumArtResult),
                });
            });
        }
    }
}
#endif
