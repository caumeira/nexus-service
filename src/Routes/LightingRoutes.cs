using Nexus.Service.Auth;
using Nexus.Service.Devices;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Models;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

public static class LightingRoutes
{
    // Presence alone made ?frozen=0 and ?frozen=false freeze the render.
    private static bool IsTruthy(Microsoft.Extensions.Primitives.StringValues v)
    {
        if (v.Count == 0) return false;
        var s = v.ToString();
        return s.Length == 0 || !(s is "0" or "false" or "False" or "FALSE");
    }

    public static void MapLightingEndpoints(this WebApplication app)
    {
        app.MapPost("/lighting/stop", (ILightingProvider l, MultiplexHub hub) => { l.StopAll(); PanelTopics.BroadcastLighting(hub); return ApiResponse.Ok(); }).AllowPanel();
        app.MapGet("/lighting/current", (ILightingProvider l) => new CurrentSyncResponse { Sync = l.GetSync(), Paused = l.IsPaused }).AllowPanel();
        // Freeze/resume the active effect's rendered frame. No-op with no active
        // effect (engine not running); returns the resulting paused state either way.
        app.MapPost("/lighting/pause", (Models.Lighting.LightingPauseBody body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.SetPaused(body.Paused);
            PanelTopics.BroadcastLighting(hub);
            return Results.Json(new Models.Lighting.LightingPauseResponse { Paused = l.IsPaused }, AppJsonContext.Default.LightingPauseResponse);
        }).AllowPanel();
        app.MapGet("/lighting/animate/settings", (Nexus.Service.Persistence.IConfigStore store) =>
            store.Load().Lighting.Animate).AllowPanel();
        app.MapGet("/lighting/static/settings", (Nexus.Service.Persistence.IConfigStore store) =>
            store.Load().Lighting.Static).AllowPanel();
        // Canonical default template bundles. The web keeps no copy of these
        // tables; it merges this over the sparse user deltas from
        // /lighting/animate/settings. Static per binary, so clients revalidate
        // against the content ETag and get cheap 304s.
        app.MapGet("/lighting/animate/defaults", (HttpRequest req, HttpResponse res) =>
        {
            var etag = AnimateTemplateDefaults.ETag;
            res.Headers.ETag = etag;
            res.Headers.CacheControl = "no-cache";
            if (string.Equals(req.Headers.IfNoneMatch.ToString(), etag, StringComparison.Ordinal))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
            return Results.Bytes(AnimateTemplateDefaults.SerializedJson, "application/json");
        }).AllowPanel();
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
            var result = l.CaptureAnimateThumbnail(key, slot, skipCache: fresh, frozen: IsTruthy(req.Query["frozen"]));
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
        // Music reactive toggle: persists the flag and starts/stops audio capture
        // only when the active effect is audio-reactive.
        app.MapPost("/lighting/music-reactive", (Models.Lighting.MusicReactiveBody body,
            ILightingProvider l,
            MultiplexHub hub) =>
        {
            l.SetMusicReactive(body.Enabled);
            PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapGet("/lighting/music-reactive", (Nexus.Service.Persistence.IConfigStore store) =>
            new Models.Lighting.MusicReactiveBody { Enabled = store.Load().Lighting.MusicReactive }).AllowPanel();
        // Blank lighting while the host sleeps. Host-only: this is a property of
        // the machine going to sleep, not something a paired phone should flip.
        app.MapGet("/lighting/sleep-blackout", (Nexus.Service.Persistence.IConfigStore store) =>
            new Models.Lighting.SleepBlackoutBody { Enabled = store.Load().Lighting.SleepBlackout }).LocalhostOnly();
        app.MapPost("/lighting/sleep-blackout", (Models.Lighting.SleepBlackoutBody body,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Lighting.SleepBlackoutCoordinator blackout) =>
        {
            store.Update(s => s.Lighting.SleepBlackout = body.Enabled);
            // Turning it off mid-blackout (only reachable if a resume event was
            // missed) must give the user their lighting back, not leave them dark.
            if (!body.Enabled)
            {
                blackout.OnResumed();
            }
            return ApiResponse.Ok();
        }).LocalhostOnly();
        // Blank lighting while the session is locked. Host-only for the same
        // reason as sleep-blackout: it is a property of this machine.
        app.MapGet("/lighting/lock-blackout", (Nexus.Service.Persistence.IConfigStore store) =>
            new Models.Lighting.LockBlackoutBody { Enabled = store.Load().Lighting.LockBlackout }).LocalhostOnly();
        app.MapPost("/lighting/lock-blackout", (Models.Lighting.LockBlackoutBody body,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Lighting.SleepBlackoutCoordinator blackout) =>
        {
            store.Update(s => s.Lighting.LockBlackout = body.Enabled);
            // Turning it off while a lock blackout is engaged (a missed unlock
            // event) must give the lighting back, not leave the user dark.
            if (!body.Enabled)
            {
                blackout.OnSessionUnlocked();
            }
            return ApiResponse.Ok();
        }).LocalhostOnly();
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
                // Gated on the daemon actually running: with no live subprocess
                // (never started, launch-failed, or between backoff respawns)
                // nothing is being scanned and the spinner would never resolve -
                // the perpetual-scanning symptom on daemon-less machines.
                Scanning = rescanning || (engine.CurrentEffectName != "none" && pm?.IsRunning == true && !devices.IsConnected),
                RgbRunning = pm?.IsRunning ?? false,
                GpuAvailable = gpu?.Available ?? false,
                // Only a thrown init is "unavailable". Before the deferred
                // warmup fires there is no attempt yet, but one is always
                // scheduled, so reporting "unavailable" for that half second
                // flashes "no usable GPU" at the user for a card that is fine.
                GpuState = gpu is null ? "unavailable"
                    : gpu.Available ? "ready"
                    : gpu.Failed ? "unavailable"
                    : "initializing",
                // Same adapter set GpuRenderSelect chooses from (0x1414 is the
                // Microsoft software adapter, never a render target).
                GpuCanSwitch = Nexus.Service.Sensors.GpuAdapterLuids.Enumerate()
                    .Count(a => a.VendorId != 0x1414) > 1,
            };
        }).AllowPanel();
        // Master brightness slider: caps every LED channel before it leaves the
        // RGB bridge (a device never renders brighter than master). Read live by
        // RgbBridge.OnFrame, so a POST takes effect on the next frame push
        // without restarting any effect.
        app.MapGet("/lighting/global-brightness", (Nexus.Service.Persistence.IConfigStore store) =>
            new Models.Lighting.GlobalBrightnessBody { Value = store.Load().Lighting.GlobalBrightness }).AllowPanel();
        app.MapPost("/lighting/global-brightness", (Models.Lighting.GlobalBrightnessBody body,
            Nexus.Service.Persistence.IConfigStore store, MultiplexHub hub) =>
        {
            // Math.Clamp(NaN, ...) returns NaN, which would propagate through
            // RgbBridge.OnFrame and zero every LED. Treat a non-finite payload
            // as "no change requested" - fall back to the documented default.
            var safe = float.IsFinite(body.Value) ? body.Value : 1.0f;
            var clamped = Math.Clamp(safe, 0f, 1f);
            store.Update(s =>
            {
                s.Lighting.GlobalBrightness = clamped;
                Nexus.Service.Lighting.LightingPresetLooks.CaptureIntoActive(s);
            });
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
#if WINDOWS
            // Forget any auto-select decision so switching back to "auto"
            // re-probes (recovers from a persisted "off" where no card worked).
            GpuRenderSelect.ClearState();
#endif
            return ApiResponse.Ok();
        }).LocalhostOnly();
        // Headless start endpoints
        app.MapPost("/lighting/animate/headless-start", (AnimateHeadlessStart body, ILightingProvider l, MultiplexHub hub, FeatureGates gates) =>
        {
            if (!gates.Lighting)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Lighting });
            }
            l.StartAnimate(body);
            PanelTopics.BroadcastLighting(hub);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();
        app.MapPost("/lighting/static/headless-start", (StaticHeadlessStart body, ILightingProvider l, MultiplexHub hub, FeatureGates gates) =>
        {
            if (!gates.Lighting)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Lighting });
            }
            try { l.StartStatic(body); }
            catch (System.ArgumentException ex) { return Results.Ok(ApiResponse.Fail(ex.Message)); }
            PanelTopics.BroadcastLighting(hub);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();
        app.MapPost("/lighting/screen/headless-start", (ScreenHeadlessStart body, ILightingProvider l, MultiplexHub hub, FeatureGates gates) =>
        {
            if (!gates.Lighting)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Lighting });
            }
            l.StartScreen(body);
            PanelTopics.BroadcastLighting(hub);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();
        // Re-open the OS screen picker to change the mirrored screen (Wayland).
        app.MapPost("/lighting/screen/reselect", (ILightingProvider l, MultiplexHub hub, FeatureGates gates) =>
        {
            if (!gates.Lighting)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Lighting });
            }
            l.ReselectScreen();
            PanelTopics.BroadcastLighting(hub);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();
        // Screen Mirror + Media post-process (hue / colorize / saturation / contrast).
        // Same shape for both modes so the right-pane Effect tab can drive either
        // with one slider set.
        app.MapGet("/lighting/screen/effect", (Nexus.Service.Persistence.IConfigStore store) =>
            store.Load().Lighting.ScreenEffect).AllowPanel();
        app.MapPost("/lighting/screen/effect", (Nexus.Service.Models.Lighting.PostProcessBody body, ILightingProvider l, MultiplexHub hub) =>
        {
            l.UpdateScreenEffect(body.Hue, body.Colorize, body.Saturation, body.Contrast, body.FlipX, body.FlipY, body.Persist, body.Reactive, body.Reactivity, body.Intensity);
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
