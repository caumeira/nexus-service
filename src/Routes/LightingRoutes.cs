using Qos.Service.Auth;
using Qos.Service.Devices;
using Qos.Service.Lighting;
using Qos.Service.Lighting.Engine.Gpu;
using Qos.Service.Models;
using Qos.Service.Models.Lighting;
using Qos.Service.Serialization;
using Qos.Service.Sockets;

namespace Qos.Service.Routes;

public static class LightingRoutes
{
    public static void MapLightingEndpoints(this WebApplication app)
    {
        app.MapPost("/lighting/stop", (ILightingProvider l, MultiplexHub hub) => { l.StopAll(); PanelTopics.BroadcastLighting(hub); return ApiResponse.Ok(); }).AllowPanel();
        app.MapPost("/lighting/frame-rate", (SetFrameRateBody body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.SetFrameRate(body.FrameRate);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        });
        app.MapPost("/lighting/scale-ratio", (SetScaleRatioBody body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.SetScaleRatio(body.Ratio);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        });
        app.MapGet("/lighting/current", (ILightingProvider l) => new CurrentSyncResponse { Sync = l.GetSync() }).AllowPanel();
        app.MapGet("/lighting/animate/settings", (Qos.Service.Persistence.IConfigStore store) =>
            store.Load().Lighting.Animate).AllowPanel();
        app.MapPost("/lighting/animate/templates", (SetAnimateTemplatesBody body, Qos.Service.Persistence.IConfigStore store, MultiplexHub hub) =>
        {
            store.Update(s => { s.Lighting.Animate.Templates = body.Templates ?? new(); });
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapGet("/lighting/static/settings", (Qos.Service.Persistence.IConfigStore store) =>
            store.Load().Lighting.StaticColor).AllowPanel();
        app.MapGet("/lighting/effects/{key}/thumbnail.bmp", (string key, ILightingProvider l, HttpRequest req) =>
        {
            var fresh = req.Query.ContainsKey("fresh");
            var bytes = l.CaptureAnimateThumbnail(key, skipCache: fresh);
            return bytes is null
                ? Results.NotFound()
                : Results.File(bytes, "image/bmp");
        }).AllowPanel();
        // Music reactive toggle: starts/stops the audio capture pipeline. When
        // off, AudioState stays at zero and every shader reverts to its idle
        // animation. One pipe in: this is the only switch the user flips.
        app.MapPost("/lighting/music-reactive", (Models.Lighting.MusicReactiveBody body,
            Qos.Service.Activity.IBeatsProvider beats,
            Qos.Service.Persistence.IConfigStore store,
            MultiplexHub hub) =>
        {
            store.Update(s => s.Lighting.MusicReactive = body.Enabled);
            if (body.Enabled)
            {
                beats.Start();
            }
            else
            {
                beats.Stop();
            }
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapGet("/lighting/music-reactive", (Qos.Service.Persistence.IConfigStore store) =>
            new Models.Lighting.MusicReactiveBody { Enabled = store.Load().Lighting.MusicReactive }).AllowPanel();
        app.MapGet("/lighting/shaders/{name}", (string name) =>
        {
            name = name.ToLowerInvariant();
            if (!ShaderLibrary.AllEffectKeys.Contains(name))
                return Results.NotFound();
            return Results.Json(new ShaderSourceResponse { Frag = ShaderLibrary.Get(name) }, AppJsonContext.Default.ShaderSourceResponse);
        }).AllowPanel();
        app.MapGet("/lighting/screen/monitors", (ILightingProvider l) => l.GetScreenSyncOptions()).AllowPanel();
        app.MapGet("/lighting/status", (Qos.Service.Lighting.Engine.LightingEngine engine, ILightingDeviceProvider devices, IServiceProvider sp) =>
        {
            // OpenRgbProcessManager only exists on Windows/macOS; resolve optionally so Linux returns false.
            var pm = sp.GetService(typeof(Qos.Service.Lighting.Rgb.OpenRgbProcessManager)) as Qos.Service.Lighting.Rgb.OpenRgbProcessManager;
            var gpu = sp.GetService(typeof(Qos.Service.Lighting.Engine.Gpu.GpuContext)) as Qos.Service.Lighting.Engine.Gpu.GpuContext;
            var bridge = sp.GetService(typeof(Qos.Service.Lighting.Rgb.RgbBridge)) as Qos.Service.Lighting.Rgb.RgbBridge;
            var rescanning = bridge?.IsRescanning ?? false;
            return new Qos.Service.Models.Cooling.LightingStatusResponse
            {
                Effect = engine.CurrentEffectName,
                Running = engine.CurrentEffectName != "none",
                // Scanning covers both first-connect and subprocess bounces so the
                // UI spinner tracks every phase where the device list is in flux.
                Scanning = rescanning || (engine.CurrentEffectName != "none" && !devices.IsConnected),
                RgbRunning = pm?.IsRunning ?? false,
                GpuAvailable = gpu?.Available ?? false,
            };
        }).AllowPanel();
        app.MapPost("/lighting/brightness", (BrightnessScale body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.SetBrightness(body);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        });
        app.MapPost("/lighting/speed", (SpeedScale body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.SetSpeed(body);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        });

        // Headless start endpoints
        app.MapPost("/lighting/static/headless-start", (StaticHeadlessStart body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.StartStatic(body);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapPost("/lighting/animate/headless-start", (AnimateHeadlessStart body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.StartAnimate(body);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapPost("/lighting/music/headless-start", (MusicHeadlessStart body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.StartMusic(body);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        });
        app.MapPost("/lighting/screen/headless-start", (ScreenHeadlessStart body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.StartScreen(body);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapPost("/lighting/gif/headless-start", (GifHeadlessStart body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.StartGif(body);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapPost("/lighting/streaming/set-streaming", (SetHeadlessStreaming body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.SetStreaming(body);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        });

        // Screen Mirror + Media post-process (hue / colorize / saturation / contrast).
        // Same shape for both modes so the right-pane Effect tab can drive either
        // with one slider set.
        app.MapGet("/lighting/screen/effect", (Qos.Service.Persistence.IConfigStore store) =>
            store.Load().Lighting.ScreenEffect).AllowPanel();
        app.MapPost("/lighting/screen/effect", (Qos.Service.Models.Lighting.PostProcessBody body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.UpdateScreenEffect(body.Hue, body.Colorize, body.Saturation, body.Contrast, body.FlipX, body.FlipY, body.Persist);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapGet("/lighting/media/effect", (Qos.Service.Persistence.IConfigStore store) =>
            store.Load().Lighting.MediaEffect).AllowPanel();
        app.MapPost("/lighting/media/effect", (Qos.Service.Models.Lighting.PostProcessBody body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.UpdateMediaEffect(body.Hue, body.Colorize, body.Saturation, body.Contrast, body.FlipX, body.FlipY, body.Persist);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
    }
}
