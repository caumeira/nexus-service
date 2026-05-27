using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Activity;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Lifecycle;

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
        BootTimer.Mark("InitializeProfiles: resolve ProfileManager");
        var profileManager = app.Services.GetRequiredService<ProfileManager>();
        BootTimer.Mark("InitializeProfiles: ProfileManager resolved");
        try
        {
            profileManager.Initialize();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[profiles] initialization failed: {ex.Message}");
        }
        BootTimer.Mark("InitializeProfiles: ProfileManager.Initialize done");

        var curveEngine = app.Services.GetRequiredService<CurveEngine>();
        BootTimer.Mark("InitializeProfiles: CurveEngine resolved");
        var lightingEngine = app.Services.GetRequiredService<LightingEngine>();
        BootTimer.Mark("InitializeProfiles: LightingEngine resolved");
        var lightingProvider = app.Services.GetRequiredService<ILightingProvider>();
        BootTimer.Mark("InitializeProfiles: ILightingProvider resolved");
        var configStore = app.Services.GetRequiredService<IConfigStore>();
        BootTimer.Mark("InitializeProfiles: IConfigStore resolved");

        // IFanControlProvider is resolved off the startup critical path by
        // LhmWarmupService (post-StartAsync BackgroundService) so its
        // transitive LhmComputer.ctor doesn't block Kestrel bind. The
        // profile-switch callback resolves it lazily; once warm it's a
        // dictionary lookup against the DI cache.
        var sp = app.Services;
        profileManager.OnProfileSwitched += () =>
        {
            try
            {
                var fans = sp.GetRequiredService<IFanControlProvider>();
                fans.ReleaseAll();
                curveEngine.ResetSmoothing();
                lightingEngine.Stop();
                // Re-engage engines with the incoming profile's settings so
                // a profile that has "silent" cooling + a plasma effect
                // resumes after the switch instead of leaving the engines
                // idle until the user clicks something.
                LiveEngineSync.Apply(configStore, fans, lightingProvider);
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
        BootTimer.Mark("WireBeatsAndPresence: resolve IBeatsProvider");
        var beatsProvider = app.Services.GetRequiredService<IBeatsProvider>();
        BootTimer.Mark("WireBeatsAndPresence: IBeatsProvider resolved");
        var muxHub = app.Services.GetRequiredService<MultiplexHub>();
        BootTimer.Mark("WireBeatsAndPresence: MultiplexHub resolved");
        beatsProvider.OnBeat += result =>
        {
            if (muxHub.TopicHasSubscribers("beats"))
            {
                var env = WsEnvelope.Build("beats", result, AppJsonContext.Default.MusicResult);
                _ = muxHub.BroadcastTopicAsync("beats", env);
            }
            if (muxHub.TopicHasSubscribers("audio"))
            {
                var snap = new Nexus.Service.Models.Lighting.AudioStateSnapshot
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
            if (topic == Nexus.Service.Panel.PanelPhonePairingService.PresenceTopic)
                PanelTopics.BroadcastPanelDevice(muxHub, "presence");
        };
        muxHub.OnTopicLastUnsubscriber += topic =>
        {
            if (topic == Nexus.Service.Panel.PanelPhonePairingService.PresenceTopic)
                PanelTopics.BroadcastPanelDevice(muxHub, "presence");
        };

        // Auto-resume Music Reactive capture if the user had it on before a restart.
        var store = app.Services.GetRequiredService<IConfigStore>();
        BootTimer.Mark("WireBeatsAndPresence: IConfigStore resolved");
        if (store.Load().Lighting.MusicReactive)
        {
            beatsProvider.Start();
            BootTimer.Mark("WireBeatsAndPresence: beatsProvider.Start (MusicReactive=true)");
        }
    }
}
