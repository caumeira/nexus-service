using System;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Lighting.Capture;
using Nexus.Service.Media;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sockets;

namespace Nexus.Service.Lighting;

/// <summary>
/// Real lighting provider — wraps the cross-platform LightingEngine and bridges
/// engine frames to the LightingOutputHub WebSocket so connected SPAs see live
/// colors. State is mirrored to disk via IConfigStore so the SPA can reload and
/// know what was running, even if engine restart-on-restart isn't wired yet.
///
/// Each /lighting/{name}/headless-start route maps to one IEffect implementation.
/// New effect modes plug in by adding an interface method, a route, and a Start*
/// implementation that calls _engine.SetEffect.
/// </summary>
public sealed class LightingProvider : ILightingProvider, IDisposable
{
    private readonly IConfigStore _store;
    private readonly LightingEngine _engine;
    private readonly LightingOutputHub _hub;
    private readonly RgbBridge? _rgb;
    private readonly GpuContext _gpu;
    private readonly MediaLibrary _media;
    private readonly IMonitorEnumerator _monitors;
    private readonly IScreenFrameSource? _frameSource;

    // Live-reactive post-process holders shared between the effect and the
    // /lighting/{mode}/effect endpoint. The endpoint mutates the fields; the
    // effect reads them each frame. Kept here so values survive across
    // start/stop cycles - the first frame after a mode restart uses whatever
    // the user had previously set.
    private readonly PostProcessState _screenPP = new();
    private readonly PostProcessState _mediaPP = new();

    public LightingProvider(IConfigStore store, LightingEngine engine, LightingOutputHub hub, GpuContext gpu, MediaLibrary media, IMonitorEnumerator monitors, IScreenFrameSource? frameSource = null, RgbBridge? rgb = null)
    {
        _store = store;
        _engine = engine;
        _hub = hub;
        _rgb = rgb;
        _gpu = gpu;
        _media = media;
        _monitors = monitors;
        _frameSource = frameSource;

        var s = _store.Load().Lighting;
        _screenPP.Set(s.ScreenEffect.Hue, s.ScreenEffect.Colorize, s.ScreenEffect.Saturation, s.ScreenEffect.Contrast, s.ScreenEffect.FlipX, s.ScreenEffect.FlipY);
        _mediaPP.Set(s.MediaEffect.Hue, s.MediaEffect.Colorize, s.MediaEffect.Saturation, s.MediaEffect.Contrast, s.MediaEffect.FlipX, s.MediaEffect.FlipY);

        _engine.OnFrame += frame => _ = _hub.BroadcastBinaryAsync(frame);
    }

    /// <summary>
    /// Bring the RGB hardware bridge online whenever a real effect starts. The
    /// bridge is null on platforms without an OpenRGB binary (macOS / Linux),
    /// in which case the call is a no-op.
    /// </summary>
    private void EnsureRgbActive() => _rgb?.Activate();

    public string GetSync() => _engine.CurrentEffectName == "none" ? _store.Load().Lighting.Sync : _engine.CurrentEffectName;

    public void SetSync(string sync) => _store.Update(s => s.Lighting.Sync = sync);

    public void StopAll()
    {
        _engine.Stop();
        _store.Update(s => s.Lighting.Sync = "none");
        _rgb?.Deactivate();
        _rgb?.AwaitShutdown();
    }

    public void SetFrameRate(int frameRate)
    {
        _store.Update(s => s.Lighting.FrameRate = frameRate);
        // Engine frame interval can be re-tuned live. Cap at 120fps so a typo
        // doesn't murder the CPU.
        var safe = Math.Clamp(frameRate, 1, 120);
        _engine.FrameIntervalMs = 1000 / safe;
    }

    public void SetScaleRatio(double ratio) =>
        _store.Update(s => s.Lighting.ScaleRatio = ratio);

    public void SetBrightness(BrightnessScale scale) => _store.Update(s =>
    {
        s.Lighting.BrightnessScale = scale.Scale;
        s.Lighting.BrightnessEnabled = scale.Enabled;
    });

    public void SetSpeed(SpeedScale scale) => _store.Update(s =>
    {
        s.Lighting.SpeedScale = scale.Scale;
        s.Lighting.SpeedEnabled = scale.Enabled;
    });

    public AnimateOptions GetAnimateOptions() => new()
    {
        Effects = new[] { "rainbow", "pulse", "wave" },
        Filters = new[] { "none" },
    };

    public AudioSyncOptions GetAudioSyncOptions() => new()
    {
        Effects = new[] { "circleramp" },
        Sources = new[] { "default" },
    };

    public ScreenSyncOptions GetScreenSyncOptions() => new()
    {
        Effects = new[] { "average" },
        Monitors = _monitors.Enumerate(),
    };

    public void StartAnimate(AnimateHeadlessStart body)
    {
        EnsureRgbActive();
        var name = (body.Effect ?? "rainbow").ToLowerInvariant();
        // Speed is bipolar: negative values run the effect in reverse. 50 = 1x
        // forward, -50 = 1x reverse, 100 = 2x forward, 0 = frozen.
        var speed = (float)(body.Speed / 50.0);
        var intensity = body.Intensity > 0 ? body.Intensity : 1f;
        var hue = body.Hue;
        var colorize = body.Colorize;
        // Do NOT coerce 0 to 1 - saturation=0 (grayscale) and contrast=0
        // (flat mid-gray) are legitimate user-selected states. The DTO
        // already defaults to 1 when the field is absent from the payload,
        // so trust the value straight through and let the shader clamp.
        var saturation = body.Saturation;
        var contrast = body.Contrast;
        var extras = ParamsToDict(body.Params);
        var effectSpeed = name == "pulse" ? speed * 0.5f : speed;

        // Fast path: if the currently running effect is the same shader, just
        // update its uniforms in place. Creating a new ShaderEffect every
        // slider drag leaks ~86KB of readback/flip buffers per cycle until GC
        // catches up, and re-triggers a full shader compile. In-place updates
        // are zero-alloc after the dict allocation for extras.
        if (_engine.CurrentEffect is ShaderEffect cur && cur.Name == name)
        {
            cur.Speed = effectSpeed;
            cur.Intensity = intensity;
            cur.Hue = hue;
            cur.Colorize = colorize;
            cur.Saturation = saturation;
            cur.Contrast = contrast;
            cur.ExtraParams = extras;
        }
        else
        {
            // BuildAnimateEffect applies its own pulse scaling, so pass the
            // pre-scaling value here to avoid scaling twice.
            _engine.SetEffect(BuildAnimateEffect(name, speed, intensity, hue, colorize, saturation, contrast, extras));
        }
        // Skip the settings write while the user is still dragging a slider.
        // The UI sends Persist=false during drag (updates go to the engine
        // in-place above) and Persist=true on release, which is the only
        // moment we need to hit disk.
        if (!body.Persist)
        {
            return;
        }
        _store.Update(s =>
        {
            s.Lighting.Sync = name;
            // Persist the full slider state for the active effect. Other
            // effects' saved states are untouched so switching back restores
            // exactly what the user last set for each one.
            s.Lighting.Animate.Effect = name;
            s.Lighting.Animate.States[name] = new Nexus.Service.Persistence.AnimateEffectState
            {
                Speed = body.Speed,
                Intensity = intensity,
                Hue = hue,
                Colorize = colorize,
                Saturation = saturation,
                Contrast = contrast,
                Params = extras is not null
                    ? new System.Collections.Generic.Dictionary<string, float>(extras)
                    : new(),
            };
        });
    }

    private static System.Collections.Generic.Dictionary<string, float>? ParamsToDict(List<ShaderParam>? list)
    {
        if (list is null || list.Count == 0)
        {
            return null;
        }
        var d = new System.Collections.Generic.Dictionary<string, float>(list.Count);
        foreach (var p in list)
        {
            if (!string.IsNullOrEmpty(p.Name))
            {
                d[p.Name] = p.Value;
            }
        }
        return d;
    }

    private ShaderEffect MakeShader(string name, string src, float speed, float intensity, float hue, float colorize, float saturation, float contrast, System.Collections.Generic.Dictionary<string, float>? extras) =>
        new ShaderEffect(name, _gpu, src)
        {
            Speed = speed,
            Intensity = intensity,
            Hue = hue,
            Colorize = colorize,
            Saturation = saturation,
            Contrast = contrast,
            ExtraParams = extras,
        };

    // Cached thumbnail BMPs keyed by effect name. First request renders ~1s of
    // the shader into an offscreen canvas, encodes to BMP, and stores the bytes.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _thumbnailCache = new();

    public byte[]? CaptureAnimateThumbnail(string key, bool skipCache = false)
    {
        var name = (key ?? "").ToLowerInvariant();
        if (!skipCache && _thumbnailCache.TryGetValue(name, out var cached))
        {
            return cached;
        }
        var defaults = DefaultParamsFor(name);
        if (defaults is null)
        {
            return null;
        }

        // Render 30 frames (~1s at 30fps) with the effect's signature hue +
        // colorize + speed so the thumbnail shows the iconic look (fire orange,
        // matrix green, nebula purple, etc.) instead of a generic rainbow.
        // The signature table mirrors SIGNATURES in
        // nexus-web/src/types/lightingTemplates.ts.
        var canvas = new Engine.CanvasBuffer(160, 90);
        var sig = SignatureFor(name);
        var thumbSpeed = sig.Speed / 50f;
        if (name == "pulse")
        {
            thumbSpeed *= 0.5f;
        }
        var effect = BuildAnimateEffect(name, thumbSpeed, sig.Intensity, sig.Hue, sig.Colorize, sig.Saturation, sig.Contrast, defaults);
        try
        {
            for (int i = 0; i < 30; i++)
            {
                effect.RenderFrame(canvas, i * 33.0);
            }
        }
        finally
        {
            try
            { effect.Dispose(); }
            catch { }
        }
        var bytes = Engine.Gpu.BmpEncoder.Encode(canvas.Pixels, canvas.Width, canvas.Height);
        if (!skipCache)
        {
            _thumbnailCache[name] = bytes;
        }
        return bytes;
    }

    /// <summary>
    /// Signature colour / speed / sat / contrast for each effect's slot 0.
    /// Drives the thumbnail render so the grid shows the iconic look of every
    /// effect. Mirrors the SIGNATURES table in lightingTemplates.ts; if the
    /// two ever drift the thumbnail will stop matching the drawer.
    /// </summary>
    private readonly record struct Signature(float Hue, float Colorize, float Speed, float Saturation, float Contrast, float Intensity);

    private static Signature SignatureFor(string name) => name switch
    {
        // Simple solid-colour fills. Slot 0 of simpleColorFeels in
        // lightingTemplates.ts: colorize 1, slow speed, saturation 1.10.
        // White is saturation 0 (pure white). These drive the thumbnail.
        "simplered"    => new(0.00f, 1.00f, 28f, 1.10f, 1.00f, 1f),
        "simpleorange" => new(0.05f, 1.00f, 28f, 1.10f, 1.00f, 1f),
        "simpleyellow" => new(0.14f, 1.00f, 28f, 1.10f, 1.00f, 1f),
        "simplegreen"  => new(0.33f, 1.00f, 28f, 1.10f, 1.00f, 1f),
        "simplecyan"   => new(0.50f, 1.00f, 28f, 1.10f, 1.00f, 1f),
        "simpleblue"   => new(0.62f, 1.00f, 28f, 1.10f, 1.00f, 1f),
        "simpleviolet" => new(0.75f, 1.00f, 28f, 1.10f, 1.00f, 1f),
        "simplepink"   => new(0.92f, 1.00f, 28f, 1.10f, 1.00f, 1f),
        "simplewhite"  => new(0.00f, 1.00f, 28f, 0.00f, 1.00f, 1f),
        "rainbow" => new(0.00f, 0.00f, 50f, 1.00f, 1.00f, 1f),
        "fire" => new(0.03f, 0.80f, 70f, 1.10f, 1.05f, 1f),
        "plasma" => new(0.85f, 0.30f, 60f, 1.00f, 1.00f, 1f),
        "spiral" => new(0.00f, 0.00f, 55f, 1.00f, 1.00f, 1f),
        "matrix" => new(0.33f, 0.75f, 80f, 1.10f, 1.10f, 1f),
        "meteor" => new(0.10f, 0.40f, 85f, 1.05f, 1.00f, 1f),
        "ripple" => new(0.55f, 0.30f, 55f, 1.00f, 1.00f, 1f),
        "wave" => new(0.58f, 0.45f, 50f, 1.00f, 1.00f, 1f),
        "gradientwave" => new(0.00f, 0.00f, 40f, 1.00f, 1.00f, 1f),
        "ball" => new(0.12f, 0.25f, 60f, 1.05f, 1.00f, 1f),
        "radar" => new(0.33f, 0.60f, 55f, 1.05f, 1.05f, 1f),
        "pulse" => new(0.55f, 0.45f, 60f, 1.00f, 1.00f, 1f),
        "watercolor" => new(0.50f, 0.30f, 40f, 1.00f, 1.00f, 1f),
        "jellyfish" => new(0.48f, 0.35f, 45f, 1.00f, 1.00f, 1f),
        "aurora" => new(0.33f, 0.35f, 45f, 1.05f, 1.00f, 1f),
        "lavalamp" => new(0.85f, 0.50f, 35f, 1.00f, 1.00f, 1f),
        "starfield" => new(0.60f, 0.25f, 55f, 1.00f, 1.00f, 1f),
        "voronoi" => new(0.40f, 0.50f, 40f, 1.05f, 1.00f, 1f),
        "neonrain" => new(0.88f, 0.50f, 85f, 1.10f, 1.05f, 1f),
        "nebula" => new(0.72f, 0.35f, 30f, 1.00f, 1.00f, 1f),
        "bursts" => new(0.08f, 0.50f, 65f, 1.05f, 1.00f, 1f),
        "lavafissure" => new(0.03f, 0.45f, 40f, 1.05f, 1.05f, 1f),
        "kaleidoscope" => new(0.00f, 0.00f, 55f, 1.00f, 1.00f, 1f),
        "wormhole" => new(0.78f, 0.40f, 70f, 1.00f, 1.00f, 1f),
        "interference" => new(0.60f, 0.55f, 65f, 1.25f, 1.15f, 1f),
        "sacredgeometry" => new(0.80f, 0.30f, 50f, 1.00f, 1.00f, 1f),
        "tessellation" => new(0.55f, 0.45f, 55f, 1.00f, 1.00f, 1f),
        "domainwarp" => new(0.70f, 0.50f, 45f, 1.15f, 1.10f, 1f),
        "inkbloom" => new(0.72f, 0.50f, 50f, 1.00f, 1.00f, 1f),
        "cosmicdust" => new(0.75f, 0.35f, 35f, 1.00f, 1.00f, 1f),
        "chromaspiral" => new(0.00f, 0.00f, 60f, 1.00f, 1.00f, 1f),
        "neongrid" => new(0.58f, 0.55f, 70f, 1.20f, 1.10f, 1f),
        "oilslick" => new(0.00f, 0.00f, 40f, 1.25f, 1.05f, 1f),
        "neoncube" => new(0.78f, 0.00f, 55f, 1.20f, 1.10f, 1f),
        "caustics" => new(0.55f, 0.30f, 35f, 1.20f, 1.10f, 1f),
        "galaxy" => new(0.72f, 0.20f, 45f, 1.20f, 1.10f, 1f),
        "starpath" => new(0.62f, 0.20f, 40f, 1.10f, 1.10f, 1f),
        "plasmaglobe" => new(0.78f, 0.30f, 60f, 1.20f, 1.10f, 1f),
        "lightning" => new(0.60f, 0.30f, 60f, 1.15f, 1.20f, 1f),
        "flowfield" => new(0.00f, 0.00f, 50f, 1.20f, 1.05f, 1f),
        "ferrofluid" => new(0.78f, 0.30f, 50f, 1.15f, 1.10f, 1f),
        "liquidchrome" => new(0.60f, 0.20f, 45f, 1.15f, 1.20f, 1f),
        "hextunnel" => new(0.55f, 0.40f, 65f, 1.20f, 1.10f, 1f),
        "mandelbrot" => new(0.00f, 0.00f, 55f, 1.10f, 1.10f, 1f),
        "circuit" => new(0.40f, 0.50f, 60f, 1.20f, 1.10f, 1f),
        "bokeh" => new(0.55f, 0.20f, 55f, 1.15f, 1.05f, 1f),
        "sandstorm" => new(0.07f, 0.55f, 50f, 1.20f, 1.10f, 1f),
        "dotmatrix" => new(0.00f, 0.00f, 55f, 1.20f, 1.10f, 1f),
        // Additional frag shaders.
        "bubbles" => new(0.55f, 0.15f, 40f, 1.15f, 1.10f, 1f),
        "silkwave" => new(0.82f, 0.30f, 50f, 1.20f, 1.10f, 1f),
        // prismwave ships as monochrome high-contrast (saturation 0) to match
        // the "stark black-and-white" look the brief calls for; slot 1 in the
        // template generator surfaces the rainbow variant.
        "prismwave" => new(0.00f, 0.00f, 55f, 0.00f, 1.65f, 1f),
        "crystaltunnel" => new(0.62f, 0.30f, 60f, 1.20f, 1.15f, 1f),
        "ribbonflow" => new(0.05f, 0.40f, 65f, 1.15f, 1.05f, 1f),
        // Audio-reactive effects. Signature values drive the thumbnail
        // render (which runs with AudioState all-zero unless a debug
        // payload is injected), so they reflect the idle look.
        "spectrumbars" => new(0.00f, 0.00f, 55f, 1.15f, 1.10f, 1f),
        "spectrumradial" => new(0.00f, 0.00f, 55f, 1.15f, 1.10f, 1f),
        "scope" => new(0.55f, 0.35f, 60f, 1.10f, 1.10f, 1f),
        "basspulse" => new(0.78f, 0.40f, 50f, 1.15f, 1.15f, 1f),
        "beatstrobe" => new(0.00f, 0.00f, 65f, 1.20f, 1.15f, 1f),
        "harmonicstar" => new(0.60f, 0.30f, 55f, 1.15f, 1.10f, 1f),
        "audiotunnel" => new(0.45f, 0.35f, 60f, 1.15f, 1.10f, 1f),
        "bassbloom" => new(0.85f, 0.35f, 45f, 1.15f, 1.10f, 1f),
        _ => new(0.00f, 0.00f, 50f, 1.00f, 1.00f, 1f),
    };

    /// <summary>
    /// Per-effect default uniform values, mirroring the EFFECTS schema on the
    /// frontend. Used by the thumbnail capture path and as the reset target.
    /// Returning null means the key isn't an animate effect.
    /// </summary>
    private static System.Collections.Generic.Dictionary<string, float>? DefaultParamsFor(string name) => name switch
    {
        // Simple solid-colour fills take no per-effect uniforms; return an
        // empty (non-null) dict so they still count as animate effects and
        // get a thumbnail rendered.
        "simplered" or "simpleorange" or "simpleyellow" or "simplegreen"
            or "simplecyan" or "simpleblue" or "simpleviolet" or "simplepink"
            or "simplewhite" => new(),
        "rainbow" => new() { ["u_density"] = 1f, ["u_rotation"] = 0f },
        "fire" => new() { ["u_turbulence"] = 1.6f },
        "plasma" => new() { ["u_warp"] = 1f, ["u_zoom"] = 1f },
        "spiral" => new() { ["u_arms"] = 5f, ["u_tightness"] = 8f },
        "matrix" => new() { ["u_columns"] = 28f, ["u_fade"] = 4f },
        "meteor" => new() { ["u_streaks"] = 6f, ["u_width"] = 0.13f },
        "ripple" => new() { ["u_freq"] = 12f },
        "wave" => new() { ["u_freq"] = 8f, ["u_amp"] = 0.18f },
        "gradientwave" => new() { ["u_freq"] = 6f },
        "ball" => new() { ["u_count"] = 8f, ["u_size"] = 0.08f },
        "radar" => new() { ["u_ringRate"] = 0.25f },
        "pulse" => new() { ["u_size"] = 1.2f },
        "watercolor" => new() { ["u_blobs"] = 6f, ["u_softness"] = 0.5f },
        "jellyfish" => new() { ["u_count"] = 3f, ["u_glow"] = 1f },
        "aurora" => new() { ["u_curtains"] = 4f, ["u_height"] = 0.55f, ["u_shimmer"] = 0.5f },
        "lavalamp" => new() { ["u_count"] = 5f, ["u_viscosity"] = 0.45f, ["u_size"] = 0.22f },
        "starfield" => new() { ["u_density"] = 45f, ["u_layers"] = 5f, ["u_trail"] = 0.6f },
        "voronoi" => new() { ["u_scale"] = 3f, ["u_edgeWidth"] = 0.05f, ["u_drift"] = 0.6f },
        "neonrain" => new() { ["u_density"] = 28f, ["u_length"] = 0.30f, ["u_splash"] = 0.85f },
        "nebula" => new() { ["u_density"] = 1f, ["u_stars"] = 0.6f, ["u_depth"] = 4f },
        "bursts" => new() { ["u_rate"] = 1.8f, ["u_particles"] = 14f, ["u_size"] = 1.1f },
        "lavafissure" => new() { ["u_flow"] = 1f, ["u_crackWidth"] = 0.35f, ["u_shimmer"] = 0.6f },
        "kaleidoscope" => new() { ["u_sides"] = 8f, ["u_spin"] = 0.4f, ["u_inner"] = 1.2f },
        "wormhole" => new() { ["u_depth"] = 1.2f, ["u_rings"] = 5f, ["u_twist"] = 0.6f },
        "interference" => new() { ["u_wavelength"] = 0.22f, ["u_sources"] = 5f },
        "sacredgeometry" => new() { ["u_layers"] = 5f, ["u_edge"] = 0.6f, ["u_pulse"] = 1f },
        "tessellation" => new() { ["u_shape"] = 0f, ["u_morph"] = 0.6f, ["u_edge"] = 0.15f },
        "domainwarp" => new() { ["u_turbulence"] = 0.8f, ["u_direction"] = 0f, ["u_bite"] = 1f },
        "inkbloom" => new() { ["u_spread"] = 1f, ["u_curl"] = 0.6f, ["u_fade"] = 0.7f },
        "cosmicdust" => new() { ["u_particles"] = 1.8f, ["u_twinkle"] = 1.5f, ["u_parallax"] = 0.6f },
        "chromaspiral" => new() { ["u_tightness"] = 5f, ["u_spin"] = 1f, ["u_bands"] = 3f },
        "neongrid" => new() { ["u_density"] = 12f, ["u_pulse"] = 1.2f, ["u_glow"] = 1.2f },
        "oilslick" => new() { ["u_flow"] = 1.5f, ["u_iridescence"] = 2.5f, ["u_scale"] = 1.5f },
        "neoncube" => new() { ["u_size"] = 0.8f, ["u_spin"] = 1.0f, ["u_glow"] = 1.0f },
        "caustics" => new() { ["u_density"] = 1.4f, ["u_brightness"] = 1.0f, ["u_flow"] = 1.0f },
        "galaxy" => new() { ["u_arms"] = 4f, ["u_dust"] = 0.8f, ["u_rotation"] = 0.5f, ["u_stars"] = 1.2f },
        "starpath" => new() { ["u_trail"] = 0.6f, ["u_axisShift"] = 0.3f, ["u_brightness"] = 1.0f },
        "plasmaglobe" => new() { ["u_branches"] = 7f, ["u_jitter"] = 1.0f, ["u_power"] = 1.0f },
        "lightning" => new() { ["u_boltRate"] = 0.8f, ["u_forks"] = 3f, ["u_glow"] = 1.0f },
        "flowfield" => new() { ["u_streams"] = 1.5f, ["u_flow"] = 1.0f, ["u_colorSpread"] = 0.6f },
        "ferrofluid" => new() { ["u_density"] = 8f, ["u_sharpness"] = 1.5f, ["u_motion"] = 1.0f },
        "liquidchrome" => new() { ["u_flow"] = 1.0f, ["u_thickness"] = 1.0f, ["u_ripple"] = 1.0f },
        "hextunnel" => new() { ["u_cellSize"] = 0.15f, ["u_zoomRate"] = 1.0f, ["u_neon"] = 1.0f },
        "mandelbrot" => new() { ["u_depth"] = 4f, ["u_rotation"] = 0.5f, ["u_brightness"] = 1.0f },
        "circuit" => new() { ["u_density"] = 9f, ["u_pulse"] = 1.0f, ["u_glow"] = 1.0f },
        "bokeh" => new() { ["u_lights"] = 16f, ["u_size"] = 0.15f, ["u_drift"] = 1.0f },
        "sandstorm" => new() { ["u_wind"] = 1.2f, ["u_density"] = 1.0f, ["u_gusts"] = 0.8f },
        "dotmatrix" => new() { ["u_density"] = 18f, ["u_scrollRate"] = 1.0f, ["u_complexity"] = 0.5f },
        // Additional frag shader defaults.
        "bubbles" => new() { ["u_count"] = 12f, ["u_rise"] = 1.0f, ["u_irid"] = 0.8f },
        "silkwave" => new() { ["u_folds"] = 6f, ["u_flow"] = 1.2f, ["u_sheen"] = 1.0f },
        "prismwave" => new() { ["u_bands"] = 4f, ["u_sharpness"] = 7f, ["u_thickness"] = 0.6f, ["u_drift"] = 0.65f },
        "crystaltunnel" => new() { ["u_facets"] = 8f, ["u_depth"] = 1.0f, ["u_refract"] = 1.0f },
        "ribbonflow" => new() { ["u_ribbons"] = 7f, ["u_turbulence"] = 1.2f, ["u_glow"] = 1.0f },
        // Audio-reactive defaults.
        "spectrumbars" => new() { ["u_bars"] = 16f, ["u_gap"] = 0.12f, ["u_glow"] = 1.0f },
        "spectrumradial" => new() { ["u_spokes"] = 32f, ["u_radius"] = 0.1f, ["u_glow"] = 1.0f },
        "scope" => new() { ["u_thickness"] = 0.012f, ["u_harmonics"] = 3f, ["u_glow"] = 1.0f },
        "basspulse" => new() { ["u_rings"] = 5f, ["u_ringSpeed"] = 1.0f, ["u_halo"] = 1.0f },
        "beatstrobe" => new() { ["u_stripes"] = 7f, ["u_flash"] = 1.2f, ["u_chroma"] = 0.5f },
        "harmonicstar" => new() { ["u_points"] = 12f, ["u_core"] = 0.1f, ["u_flare"] = 1.0f },
        "audiotunnel" => new() { ["u_ringDensity"] = 6f, ["u_twist"] = 0.8f, ["u_neon"] = 1.0f },
        "bassbloom" => new() { ["u_petals"] = 7f, ["u_shimmer"] = 1.0f, ["u_bloomSize"] = 0.4f },
        _ => null,
    };

    private IEffect BuildAnimateEffect(string name, float speed, float intensity, float hue, float colorize, float saturation, float contrast, System.Collections.Generic.Dictionary<string, float>? extras)
    {
        // Every animate mode is a fragment-only GLSL shader. GPU-only - if
        // the context can't initialise, the canvas stays dark. Pulse gets
        // a 0.5x speed scale to match the legacy feel.
        var effectSpeed = name == "pulse" ? speed * 0.5f : speed;
        // Simple solid-colour fills all share one cheap shader; the colour is
        // carried by the post-process tint, not the GLSL.
        if (name.StartsWith("simple", System.StringComparison.Ordinal))
        {
            return MakeShader(name, ShaderLibrary.Get(name), effectSpeed, intensity, hue, colorize, saturation, contrast, extras);
        }
        var src = name switch
        {
            "pulse" => ShaderLibrary.Pulse,
            "fire" => ShaderLibrary.Fire,
            "plasma" => ShaderLibrary.Plasma,
            "spiral" => ShaderLibrary.Spiral,
            "matrix" => ShaderLibrary.Matrix,
            "meteor" => ShaderLibrary.Meteor,
            "ripple" => ShaderLibrary.Ripple,
            "wave" => ShaderLibrary.Wave,
            "gradientwave" => ShaderLibrary.GradientWave,
            "ball" => ShaderLibrary.Ball,
            "radar" => ShaderLibrary.Radar,
            "watercolor" => ShaderLibrary.Watercolor,
            "jellyfish" => ShaderLibrary.Jellyfish,
            "aurora" => ShaderLibrary.Aurora,
            "lavalamp" => ShaderLibrary.LavaLamp,
            "starfield" => ShaderLibrary.Starfield,
            "voronoi" => ShaderLibrary.VoronoiCells,
            "neonrain" => ShaderLibrary.NeonRain,
            "bursts" => ShaderLibrary.Bursts,
            "nebula" => ShaderLibrary.Nebula,
            "lavafissure" => ShaderLibrary.LavaFissure,
            "kaleidoscope" => ShaderLibrary.Kaleidoscope,
            "wormhole" => ShaderLibrary.Wormhole,
            "interference" => ShaderLibrary.Interference,
            "sacredgeometry" => ShaderLibrary.SacredGeometry,
            "tessellation" => ShaderLibrary.Tessellation,
            "domainwarp" => ShaderLibrary.DomainWarp,
            "inkbloom" => ShaderLibrary.InkBloom,
            "cosmicdust" => ShaderLibrary.CosmicDust,
            "chromaspiral" => ShaderLibrary.ChromaSpiral,
            "neongrid" => ShaderLibrary.NeonGrid,
            "oilslick" => ShaderLibrary.OilSlick,
            "neoncube" => ShaderLibrary.NeonCube,
            "caustics" => ShaderLibrary.Get("caustics"),
            "galaxy" => ShaderLibrary.Get("galaxy"),
            "starpath" => ShaderLibrary.Get("starpath"),
            "plasmaglobe" => ShaderLibrary.Get("plasmaglobe"),
            "lightning" => ShaderLibrary.Get("lightning"),
            "flowfield" => ShaderLibrary.Get("flowfield"),
            "ferrofluid" => ShaderLibrary.Get("ferrofluid"),
            "liquidchrome" => ShaderLibrary.Get("liquidchrome"),
            "hextunnel" => ShaderLibrary.Get("hextunnel"),
            "mandelbrot" => ShaderLibrary.Get("mandelbrot"),
            "circuit" => ShaderLibrary.Get("circuit"),
            "bokeh" => ShaderLibrary.Get("bokeh"),
            "sandstorm" => ShaderLibrary.Get("sandstorm"),
            "dotmatrix" => ShaderLibrary.Get("dotmatrix"),
            "spectrumbars" => ShaderLibrary.Get("spectrumbars"),
            "spectrumradial" => ShaderLibrary.Get("spectrumradial"),
            "scope" => ShaderLibrary.Get("scope"),
            "basspulse" => ShaderLibrary.Get("basspulse"),
            "beatstrobe" => ShaderLibrary.Get("beatstrobe"),
            "harmonicstar" => ShaderLibrary.Get("harmonicstar"),
            "audiotunnel" => ShaderLibrary.Get("audiotunnel"),
            "bassbloom" => ShaderLibrary.Get("bassbloom"),
            "bubbles" => ShaderLibrary.Bubbles,
            "silkwave" => ShaderLibrary.SilkWave,
            "prismwave" => ShaderLibrary.PrismWave,
            "crystaltunnel" => ShaderLibrary.CrystalTunnel,
            "ribbonflow" => ShaderLibrary.RibbonFlow,
            _ => ShaderLibrary.Rainbow,
        };
        return MakeShader(name, src, effectSpeed, intensity, hue, colorize, saturation, contrast, extras);
    }

    public void StartMusic(MusicHeadlessStart body)
    {
        // No audio capture impl yet. Persist intent so the SPA can reflect it,
        // but the engine doesn't render anything.
        _store.Update(s => s.Lighting.Sync = "music");
    }

    public void StartScreen(ScreenHeadlessStart body)
    {
        EnsureRgbActive();
        _engine.FrameIntervalMs = 16;
        // Do NOT overwrite _screenPP from body. The post-process holder is the
        // authoritative live state; it was loaded from settings at construction
        // and is mutated only by the /lighting/screen/effect endpoint. If a
        // client calls /start with empty post-process fields before it has
        // fetched the current values, overwriting here would silently clobber
        // the user's saved look back to identity on every mode swap.
        _engine.SetEffect(new ScreenMirrorEffect(body.Monitor, _screenPP, _frameSource));
        _store.Update(s => s.Lighting.Sync = "screen");
    }

    /// <summary>
    /// Returns the live Screen Mirror post-process. Aliased reference so the
    /// endpoint can write through it without a subsequent /start call.
    /// </summary>
    public PostProcessState ScreenPostProcess => _screenPP;

    /// <summary>Same alias for the Media post-process holder.</summary>
    public PostProcessState MediaPostProcess => _mediaPP;

    public void UpdateScreenEffect(float hue, float colorize, float saturation, float contrast, bool flipX, bool flipY, bool persist)
    {
        _screenPP.Set(hue, colorize, saturation, contrast, flipX, flipY);
        if (!persist)
            return;
        _store.Update(s =>
        {
            s.Lighting.ScreenEffect.Hue = hue;
            s.Lighting.ScreenEffect.Colorize = colorize;
            s.Lighting.ScreenEffect.Saturation = saturation;
            s.Lighting.ScreenEffect.Contrast = contrast;
            s.Lighting.ScreenEffect.FlipX = flipX;
            s.Lighting.ScreenEffect.FlipY = flipY;
        });
    }

    public void UpdateMediaEffect(float hue, float colorize, float saturation, float contrast, bool flipX, bool flipY, bool persist)
    {
        _mediaPP.Set(hue, colorize, saturation, contrast, flipX, flipY);
        if (!persist)
            return;
        _store.Update(s =>
        {
            s.Lighting.MediaEffect.Hue = hue;
            s.Lighting.MediaEffect.Colorize = colorize;
            s.Lighting.MediaEffect.Saturation = saturation;
            s.Lighting.MediaEffect.Contrast = contrast;
            s.Lighting.MediaEffect.FlipX = flipX;
            s.Lighting.MediaEffect.FlipY = flipY;
        });
    }

    public void StartGif(GifHeadlessStart body)
    {
        EnsureRgbActive();
        if (body.Paths.Count > 0 && System.IO.File.Exists(body.Paths[0]))
        {
            _engine.SetEffect(new GifEffect(body.Paths[0]));
        }
        _store.Update(s => s.Lighting.Sync = "gif");
    }

    public bool StartMedia(string mediaId)
    {
        var item = _media.GetItem(mediaId);
        if (item is null)
        {
            return false;
        }
        var framesPath = _media.GetFramesBinPath(mediaId);
        if (!System.IO.File.Exists(framesPath))
        {
            return false;
        }

        EnsureRgbActive();
        _engine.SetEffect(new MediaFramesEffect(framesPath, item.Width, item.Height, item.Fps, _mediaPP));
        _store.Update(s =>
        {
            s.Lighting.Sync = "media";
            s.Lighting.LastMediaId = mediaId;
        });
        return true;
    }

    public void SetStreaming(SetHeadlessStreaming body) { /* no-op for the virtual strip */ }

    public void Dispose()
    {
        _rgb?.Dispose();
        _engine.Dispose();
    }
}
