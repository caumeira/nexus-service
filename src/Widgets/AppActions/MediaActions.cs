using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Activity;
using Nexus.Service.Models.Activity;
using Nexus.Service.Serialization;
using static Nexus.Service.Widgets.AppActions.AppActionHelpers;

namespace Nexus.Service.Widgets.AppActions;

/// <summary>Host actions for media: read the focused now-playing session,
/// drive transport, set system volume. Gated through the manifest's
/// capabilities.dispatch allowlist by the dispatch route.</summary>
public static class MediaActions
{
    public static void RegisterAll(AppActionRegistry registry)
    {
        registry.Register("media.nowPlaying", (services, _, _) =>
        {
            var media = services.GetRequiredService<IMediaProvider>();
            var s = Focused(media);
            if (s is null) return Task.FromResult<JsonElement?>(null);
            var json = JsonSerializer.Serialize(s, AppJsonContext.Default.MediaSession);
            using var doc = JsonDocument.Parse(json);
            return Task.FromResult<JsonElement?>(doc.RootElement.Clone());
        });

        registry.Register("media.transport", (services, args, _) =>
        {
            var action = Str(args, "action");
            if (string.IsNullOrEmpty(action))
                return Task.FromResult<JsonElement?>(Ack(false, "missing action"));
            var media = services.GetRequiredService<IMediaProvider>();
            var source = Str(args, "source");
            if (string.IsNullOrEmpty(source)) source = Focused(media)?.SourceAppName;
            if (string.IsNullOrEmpty(source))
                return Task.FromResult<JsonElement?>(Ack(false, "no active media"));
            media.Control(source!, action!);
            return Task.FromResult<JsonElement?>(Ack(true));
        });

        registry.Register("media.setVolume", (services, args, _) =>
        {
            var v = Num(args, "value");
            if (v is null || v < 0 || v > 1)
                return Task.FromResult<JsonElement?>(Ack(false, "value must be 0..1"));
            var vol = services.GetRequiredService<IVolumeProvider>();
            vol.SetVolume(v.Value);
            var json = JsonSerializer.Serialize(vol.GetState(), AppJsonContext.Default.VolumeState);
            using var doc = JsonDocument.Parse(json);
            return Task.FromResult<JsonElement?>(doc.RootElement.Clone());
        });
    }

    private static MediaSession? Focused(IMediaProvider media)
    {
        MediaSession? first = null;
        foreach (var kv in media.GetSessions())
        {
            if (kv.Value.IsFocused) return kv.Value;
            first ??= kv.Value;
        }
        return first;
    }

    public static IReadOnlyList<string> AllActions => new[] { "media.nowPlaying", "media.transport", "media.setVolume" };
}
