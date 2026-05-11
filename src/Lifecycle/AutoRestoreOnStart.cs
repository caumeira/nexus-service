using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Qos.Service.Cooling;
using Qos.Service.Lighting;
using Qos.Service.Models.Common;
using Qos.Service.Models.Lighting;
using Qos.Service.Persistence;

namespace Qos.Service.Lifecycle;

/// <summary>
/// One-shot startup task that replays the persisted lighting + cooling state
/// to the hardware. Without this, the engines come up with empty in-memory
/// state and the user has to visit each tab to "kick" the saved profile.
///
/// Runs after a short delay so the fan provider has enumerated channels and
/// the RGB bridge is initialized (CurveEngine itself waits 3s for the same
/// reason).
/// </summary>
internal sealed class AutoRestoreOnStart : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(4);

    private readonly IConfigStore _store;
    private readonly ILightingProvider _lighting;
    private readonly IFanControlProvider _fans;

    // Snapshot at construction so we can tell, when ExecuteAsync wakes up
    // 4s later, whether the user changed anything in the meantime via the
    // dashboard. If they did, we leave their choice in place instead of
    // stomping it with the previously-persisted value.
    private readonly string _coolingPresetAtBoot;
    private readonly string _lightingSyncAtBoot;

    public AutoRestoreOnStart(
        IConfigStore store,
        ILightingProvider lighting,
        IFanControlProvider fans)
    {
        _store = store;
        _lighting = lighting;
        _fans = fans;
        var initial = _store.Load();
        _coolingPresetAtBoot = initial.Cooling.ActivePreset ?? "";
        _lightingSyncAtBoot = initial.Lighting.Sync ?? "";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }

        try { RestoreCooling(); }
        catch (Exception ex) { Console.Error.WriteLine($"[auto-restore] cooling failed: {ex.Message}"); }

        try { RestoreLighting(); }
        catch (Exception ex) { Console.Error.WriteLine($"[auto-restore] lighting failed: {ex.Message}"); }
    }

    private void RestoreCooling()
    {
        var current = _store.Load().Cooling.ActivePreset ?? "";
        // If the user picked a different preset in the dashboard during the
        // 4s init window, respect their choice.
        if (!string.Equals(current, _coolingPresetAtBoot, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[auto-restore] cooling preset changed since boot ({_coolingPresetAtBoot} -> {current}), leaving as-is");
            return;
        }
        // "custom" needs no re-apply (the curves already carry their fan
        // assignments). "off" was already idle; skipping avoids stomping on
        // a user who manually set a fan duty before we reached this point.
        if (current is "silent" or "balanced" or "performance")
        {
            FanProfiles.Apply(current, _fans, _store);
            Console.WriteLine($"[auto-restore] cooling preset re-applied: {current}");
        }
    }

    private void RestoreLighting()
    {
        var s = _store.Load().Lighting;
        var sync = s.Sync ?? "";
        // Same guard as cooling: if the user already kicked off a different
        // effect via the dashboard, don't overwrite it.
        if (!string.Equals(sync, _lightingSyncAtBoot, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[auto-restore] lighting sync changed since boot ({_lightingSyncAtBoot} -> {sync}), leaving as-is");
            return;
        }
        if (string.IsNullOrEmpty(sync) || string.Equals(sync, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        switch (sync.ToLowerInvariant())
        {
            case "static":
                _lighting.StartStatic(new StaticHeadlessStart
                {
                    Color = new RGBA { R = s.StaticColor.R, G = s.StaticColor.G, B = s.StaticColor.B },
                });
                Console.WriteLine("[auto-restore] lighting: static");
                break;

            case "music":
                _lighting.StartMusic(new MusicHeadlessStart());
                Console.WriteLine("[auto-restore] lighting: music");
                break;

            case "screen":
                _lighting.StartScreen(new ScreenHeadlessStart());
                Console.WriteLine("[auto-restore] lighting: screen");
                break;

            case "gif":
                // No persisted path list to restore from; explicit case keeps
                // "gif" from falling through to the shader-name default below.
                break;

            case "media":
                var mediaId = s.LastMediaId;
                if (!string.IsNullOrEmpty(mediaId))
                {
                    if (_lighting.StartMedia(mediaId))
                    {
                        Console.WriteLine($"[auto-restore] lighting: media ({mediaId})");
                    }
                    else
                    {
                        Console.WriteLine($"[auto-restore] lighting: media item {mediaId} missing, skipped");
                    }
                }
                break;

            default:
                // Animate: Sync is the shader effect name. Pull the saved
                // slider state for that effect; fall back to defaults if the
                // user never explicitly touched it.
                var effect = sync;
                if (!s.Animate.States.TryGetValue(effect, out var saved) || saved is null)
                {
                    saved = new AnimateEffectState();
                }
                _lighting.StartAnimate(new AnimateHeadlessStart
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
                Console.WriteLine($"[auto-restore] lighting: animate/{effect}");
                break;
        }
    }

    private static List<ShaderParam> AnimateParamsToList(Dictionary<string, float>? d)
    {
        var list = new List<ShaderParam>();
        if (d is null) return list;
        foreach (var kv in d)
        {
            list.Add(new ShaderParam { Name = kv.Key, Value = kv.Value });
        }
        return list;
    }
}
