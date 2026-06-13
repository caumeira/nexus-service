using Nexus.Service.Auth;
using Nexus.Service.Devices;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Models;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

public static class LightingRoutes
{
    public static void MapLightingEndpoints(this WebApplication app)
    {
        app.MapPost("/lighting/stop", (ILightingProvider l, MultiplexHub hub) => { l.StopAll(); PanelTopics.BroadcastLighting(hub); return ApiResponse.Ok(); }).AllowPanel();
        app.MapGet("/lighting/current", (ILightingProvider l) => new CurrentSyncResponse { Sync = l.GetSync() }).AllowPanel();
        app.MapGet("/lighting/animate/settings", (Nexus.Service.Persistence.IConfigStore store) =>
            store.Load().Lighting.Animate).AllowPanel();
        app.MapPost("/lighting/animate/templates", (SetAnimateTemplatesBody body, ILightingProvider l, MultiplexHub hub) =>
        {
            // Persist + reconcile: if the edited slot is the one driving the LEDs,
            // the provider pushes the new look to the running shader in place.
            l.SaveAnimateTemplates(body.Templates ?? new());
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapGet("/lighting/effects/{key}/thumbnail.bmp", (string key, ILightingProvider l, HttpRequest req, HttpResponse res) =>
        {
            var fresh = req.Query.ContainsKey("fresh");
            // Preset slot to render (universal: 4 per effect). ?v is the client's
            // content-bust token for the browser cache; the ETag below is the
            // service's own freshness check.
            var slot = int.TryParse(req.Query["slot"], out var sv) ? sv : 0;
            var result = l.CaptureAnimateThumbnail(key, slot, skipCache: fresh);
            if (result is null) return Results.NotFound();
            var (bytes, tag) = result.Value;
            var etag = $"\"{tag}\"";
            // ETag is the saved look's content hash + must-revalidate, so the
            // browser may store the BMP but always rechecks: an edit is never
            // pinned behind the old image, even when a surface keeps requesting
            // the same ?v token. Unchanged looks come back as a cheap 304.
            res.Headers.ETag = etag;
            if (!fresh)
            {
                res.Headers.CacheControl = "no-cache";
                if (string.Equals(req.Headers.IfNoneMatch.ToString(), etag, StringComparison.Ordinal))
                {
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }
            }
            else
            {
                res.Headers.CacheControl = "no-store";
            }
            return Results.File(bytes, "image/bmp");
        }).AllowPanel();
        // Music reactive toggle: starts/stops the audio capture pipeline. When
        // off, AudioState stays at zero and every shader reverts to its idle
        // animation. One pipe in: this is the only switch the user flips.
        app.MapPost("/lighting/music-reactive", (Models.Lighting.MusicReactiveBody body,
            Nexus.Service.Activity.IBeatsProvider beats,
            Nexus.Service.Persistence.IConfigStore store,
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
        app.MapGet("/lighting/music-reactive", (Nexus.Service.Persistence.IConfigStore store) =>
            new Models.Lighting.MusicReactiveBody { Enabled = store.Load().Lighting.MusicReactive }).AllowPanel();
        app.MapGet("/lighting/shaders/{name}", (string name) =>
        {
            name = name.ToLowerInvariant();
            if (!ShaderLibrary.AllEffectKeys.Contains(name))
                return Results.NotFound();
            return Results.Json(new ShaderSourceResponse { Frag = ShaderLibrary.Get(name) }, AppJsonContext.Default.ShaderSourceResponse);
        }).AllowPanel();
        app.MapGet("/lighting/screen/monitors", (ILightingProvider l) => l.GetScreenSyncOptions()).AllowPanel();
        app.MapGet("/lighting/status", (Nexus.Service.Lighting.Engine.LightingEngine engine, ILightingDeviceProvider devices, IServiceProvider sp) =>
        {
            // OpenRgbProcessManager only exists on Windows/macOS; resolve optionally so Linux returns false.
            var pm = sp.GetService(typeof(Nexus.Service.Lighting.Rgb.OpenRgbProcessManager)) as Nexus.Service.Lighting.Rgb.OpenRgbProcessManager;
            var gpu = sp.GetService(typeof(Nexus.Service.Lighting.Engine.Gpu.GpuContext)) as Nexus.Service.Lighting.Engine.Gpu.GpuContext;
            var bridge = sp.GetService(typeof(Nexus.Service.Lighting.Rgb.RgbBridge)) as Nexus.Service.Lighting.Rgb.RgbBridge;
            var rescanning = bridge?.IsRescanning ?? false;
            return new Nexus.Service.Models.Cooling.LightingStatusResponse
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
        // Master brightness slider: multiplies every LED channel before it leaves
        // the RGB bridge. Read live by RgbBridge.OnFrame, so a POST takes effect
        // on the next frame push without restarting any effect.
        app.MapGet("/lighting/global-brightness", (Nexus.Service.Persistence.IConfigStore store) =>
            new Models.Lighting.GlobalBrightnessBody { Value = store.Load().Lighting.GlobalBrightness }).AllowPanel();
        app.MapPost("/lighting/global-brightness", (Models.Lighting.GlobalBrightnessBody body,
            Nexus.Service.Persistence.IConfigStore store, MultiplexHub hub) =>
        {
            // Math.Clamp(NaN, ...) returns NaN, which would propagate through
            // RgbBridge.OnFrame and zero every LED. Treat a non-finite payload
            // as "no change requested" — fall back to the documented default.
            var safe = float.IsFinite(body.Value) ? body.Value : 1.0f;
            var clamped = Math.Clamp(safe, 0f, 1f);
            store.Update(s => s.Lighting.GlobalBrightness = clamped);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        // Render-GPU selection (which card runs the lighting shaders). Host-only
        // (LocalhostOnly) -- a paired phone must not flip the host's GPU. The POST
        // persists + writes the OS preference; applying it needs a service restart
        // (POST /service/restart), since the GL context is created once at boot.
        app.MapGet("/lighting/render-gpu", (Nexus.Service.Persistence.IConfigStore store) =>
            new Models.Lighting.RenderGpuBody { Value = store.Load().Lighting.RenderGpu }).LocalhostOnly();
        app.MapPost("/lighting/render-gpu", (Models.Lighting.RenderGpuBody body,
            Nexus.Service.Persistence.IConfigStore store, Nexus.Service.Sensors.ISensorProvider sensors) =>
        {
            var value = string.IsNullOrWhiteSpace(body.Value) ? "auto" : body.Value.Trim();
            store.Update(s => s.Lighting.RenderGpu = value);
            GpuRenderPreference.Apply(value, sensors.GetGpus());
            return ApiResponse.Ok();
        }).LocalhostOnly();
        // Headless start endpoints
        app.MapPost("/lighting/animate/headless-start", (AnimateHeadlessStart body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.StartAnimate(body);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapPost("/lighting/screen/headless-start", (ScreenHeadlessStart body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.StartScreen(body);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        // Re-open the OS screen picker to change the mirrored screen (Wayland).
        app.MapPost("/lighting/screen/reselect", (ILightingProvider l, MultiplexHub hub) =>
        {
            l.ReselectScreen();
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapPost("/lighting/gif/headless-start", (GifHeadlessStart body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.StartGif(body);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        // Screen Mirror + Media post-process (hue / colorize / saturation / contrast).
        // Same shape for both modes so the right-pane Effect tab can drive either
        // with one slider set.
        app.MapGet("/lighting/screen/effect", (Nexus.Service.Persistence.IConfigStore store) =>
            store.Load().Lighting.ScreenEffect).AllowPanel();
        app.MapPost("/lighting/screen/effect", (Nexus.Service.Models.Lighting.PostProcessBody body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.UpdateScreenEffect(body.Hue, body.Colorize, body.Saturation, body.Contrast, body.FlipX, body.FlipY, body.Persist);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapGet("/lighting/media/effect", (Nexus.Service.Persistence.IConfigStore store) =>
            store.Load().Lighting.MediaEffect).AllowPanel();
        app.MapPost("/lighting/media/effect", (Nexus.Service.Models.Lighting.PostProcessBody body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.UpdateMediaEffect(body.Hue, body.Colorize, body.Saturation, body.Contrast, body.FlipX, body.FlipY, body.Persist);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
    }
}
