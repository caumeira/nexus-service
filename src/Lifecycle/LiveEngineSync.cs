using System;
using System.Collections.Generic;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Common;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Engages the live cooling + lighting engines from the current persisted
/// settings. Used by flows that change settings out from under the engines
/// without going through a user-driven endpoint that would otherwise apply
/// the new state: profile switch (the swap stops engines but never restarts
/// them with the new profile's values) and profile / category reset (settings
/// flip to defaults but the engines stay idle).
///
/// Mirrors the subset of <see cref="AutoRestoreOnStart"/> that re-engages the
/// engines from store state. Kept here so callers triggered by user actions
/// can run synchronously instead of deferring to the boot-time background
/// task.
/// </summary>
public static class LiveEngineSync
{
    /// <summary>Apply cooling preset + lighting sync from the current store state.</summary>
    public static void Apply(IConfigStore store, IFanControlProvider fans, ILightingProvider lighting)
    {
        ApplyCooling(store, fans);
        ApplyLighting(store, lighting);
    }

    private static void ApplyCooling(IConfigStore store, IFanControlProvider fans)
    {
        try
        {
            var preset = (store.Load().Cooling.ActivePreset ?? "").ToLowerInvariant();
            if (preset is "silent" or "balanced" or "turbo")
            {
                FanProfiles.Apply(preset, fans, store);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[live-sync] cooling failed: {ex.Message}");
        }
    }

    private static void ApplyLighting(IConfigStore store, ILightingProvider lighting)
    {
        try
        {
            var s = store.Load().Lighting;
            var sync = (s.Sync ?? "").ToLowerInvariant();
            if (string.IsNullOrEmpty(sync) || sync == "none") return;

            switch (sync)
            {
                case "music":
                    lighting.StartMusic(new MusicHeadlessStart());
                    break;
                case "screen":
                    lighting.StartScreen(new ScreenHeadlessStart());
                    break;
                case "gif":
                    // No persisted path list to restore from.
                    break;
                case "media":
                    var mediaId = s.LastMediaId;
                    if (!string.IsNullOrEmpty(mediaId)) lighting.StartMedia(mediaId);
                    break;
                default:
                    // Animate: Sync is the shader effect name. Pull the saved
                    // slider state for that effect; fall back to defaults if
                    // the user has never touched it.
                    var effect = sync;
                    if (!s.Animate.States.TryGetValue(effect, out var saved) || saved is null)
                    {
                        saved = new AnimateEffectState();
                    }
                    lighting.StartAnimate(new AnimateHeadlessStart
                    {
                        Effect = effect,
                        Speed = saved.Speed,
                        Intensity = saved.Intensity,
                        Hue = saved.Hue,
                        Colorize = saved.Colorize,
                        Saturation = saved.Saturation,
                        Contrast = saved.Contrast,
                        Params = AnimateParamsToList(saved.Params),
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[live-sync] lighting failed: {ex.Message}");
        }
    }

    private static List<ShaderParam> AnimateParamsToList(Dictionary<string, float>? d)
    {
        var list = new List<ShaderParam>();
        if (d is null) return list;
        foreach (var kv in d) list.Add(new ShaderParam { Name = kv.Key, Value = kv.Value });
        return list;
    }
}
