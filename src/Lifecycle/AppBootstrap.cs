using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Qos.Service.Activity;
using Qos.Service.Cooling;
using Qos.Service.Lighting;
using Qos.Service.Lighting.Engine;
using Qos.Service.Lighting.Engine.Gpu;
using Qos.Service.Persistence;
using Qos.Service.Serialization;
using Qos.Service.Sockets;

namespace Qos.Service.Lifecycle;

// Post-Build wiring that runs once before the middleware pipeline goes up:
// GPU eager-init, profile manager init + reapply hook, BeatsProvider/audio
// → MultiplexHub event bridge, panel-presence topic refresh.
internal static class AppBootstrap
{
    // macOS AppKit throws 'NSWindow should only be instantiated on the main
    // thread' if GLFW tries to create its hidden window from a thread-pool
    // thread later. Windows / Linux don't care, but paying the ~50ms init
    // cost here on startup is cheap.
    public static void EagerInitGpu(WebApplication app)
    {
        Console.WriteLine("[gpu] pre-init on main thread…");
        try
        {
            var gpu = app.Services.GetRequiredService<GpuContext>();
            lock (gpu.Lock)
            {
                gpu.EnsureInitializedLocked();
            }
            Console.WriteLine($"[gpu] pre-init done, available={app.Services.GetRequiredService<GpuContext>().Available}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[gpu] pre-init threw: {ex}");
        }
    }

    // Creates Default profile if none exists, then wires the profile-switch
    // hook to release fan control, reset curve smoothing, and stop the
    // lighting engine so the incoming profile starts clean.
    public static void InitializeProfiles(WebApplication app)
    {
        var profileManager = app.Services.GetRequiredService<ProfileManager>();
        try
        {
            profileManager.Initialize();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[profiles] initialization failed: {ex.Message}");
        }

        var fans = app.Services.GetRequiredService<IFanControlProvider>();
        var curveEngine = app.Services.GetRequiredService<CurveEngine>();
        var lightingEngine = app.Services.GetRequiredService<LightingEngine>();
        profileManager.OnProfileSwitched += () =>
        {
            try
            {
                fans.ReleaseAll();
                curveEngine.ResetSmoothing();
                lightingEngine.Stop();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[profiles] reapply failed: {ex.Message}");
            }
        };
    }

    // BeatsProvider start/stop tracks the "beats" topic subscriber count.
    // OnBeat fans out to two topics: "beats" (raw MusicResult) and "audio"
    // (the full level/bass/mid/high/beat/spectrum snapshot). Phone-presence
    // is wired here too because it shares the same hub.
    public static void WireBeatsAndPresence(WebApplication app)
    {
        var beatsProvider = app.Services.GetRequiredService<IBeatsProvider>();
        var muxHub = app.Services.GetRequiredService<MultiplexHub>();
        beatsProvider.OnBeat += result =>
        {
            if (muxHub.TopicHasSubscribers("beats"))
            {
                var env = WsEnvelope.Build("beats", result, AppJsonContext.Default.MusicResult);
                _ = muxHub.BroadcastTopicAsync("beats", env);
            }
            if (muxHub.TopicHasSubscribers("audio"))
            {
                var snap = new Qos.Service.Models.Lighting.AudioStateSnapshot
                {
                    Level = AudioState.Level,
                    Bass = AudioState.Bass,
                    Mid = AudioState.Mid,
                    High = AudioState.High,
                    Beat = AudioState.Beat,
                    Spectrum = new List<float>(AudioState.Spectrum),
                };
                var audioEnv = WsEnvelope.Build("audio", snap, AppJsonContext.Default.AudioStateSnapshot);
                _ = muxHub.BroadcastTopicAsync("audio", audioEnv);
            }
        };
        muxHub.OnTopicFirstSubscriber += topic => { if (topic == "beats") beatsProvider.Start(); };
        muxHub.OnTopicLastUnsubscriber += topic => { if (topic == "beats" && !muxHub.TopicHasSubscribers("beats")) beatsProvider.Stop(); };

        // Phone presence: when the first phone subscribes (or the last leaves)
        // the dashboard's connected-count needs to refresh. Reuse the existing
        // panel/device topic so usePanelDevices subscribes once and gets both
        // the device-list and the connected-count signal.
        muxHub.OnTopicFirstSubscriber += topic =>
        {
            if (topic == Qos.Service.Panel.PanelPhonePairingService.PresenceTopic)
                PanelTopics.BroadcastPanelDevice(muxHub, "presence");
        };
        muxHub.OnTopicLastUnsubscriber += topic =>
        {
            if (topic == Qos.Service.Panel.PanelPhonePairingService.PresenceTopic)
                PanelTopics.BroadcastPanelDevice(muxHub, "presence");
        };

        // Auto-resume Music Reactive capture if the user had it on before a restart.
        var store = app.Services.GetRequiredService<IConfigStore>();
        if (store.Load().Lighting.MusicReactive)
        {
            beatsProvider.Start();
        }
    }
}
