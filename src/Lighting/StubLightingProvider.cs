using Qos.Service.Models.Lighting;
using Qos.Service.Persistence;

namespace Qos.Service.Lighting;

/// <summary>
/// Persistence-backed lighting stub. Every "start X" call records the new sync
/// mode so /lighting/current returns the right value, but no actual frames are
/// rendered. brightness/speed/frame-rate/scale-ratio also persist so the SPA
/// can round-trip them.
/// </summary>
public sealed class StubLightingProvider : ILightingProvider
{
    private readonly IConfigStore _store;

    public StubLightingProvider(IConfigStore store) { _store = store; }

    public string GetSync() => _store.Load().Lighting.Sync;
    public void SetSync(string sync) => _store.Update(s => s.Lighting.Sync = sync);
    public void StopAll() => _store.Update(s => s.Lighting.Sync = "none");

    public void SetFrameRate(int frameRate) => _store.Update(s => s.Lighting.FrameRate = frameRate);
    public void SetScaleRatio(double ratio) => _store.Update(s => s.Lighting.ScaleRatio = ratio);

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
        Effects = new[] { "Wave", "Rainbow", "Pulse", "Ripple", "Snake", "Fade", "Static" },
        Filters = new[] { "None", "Glow", "Blur" },
    };

    public AudioSyncOptions GetAudioSyncOptions() => new()
    {
        Effects = new[] { "CircleRamp", "Spectrum", "Pulse", "Energy" },
        Sources = new[] { "default" },
    };

    public ScreenSyncOptions GetScreenSyncOptions() => new()
    {
        Effects = new[] { "Average", "Bars", "Mood" },
        Monitors = new System.Collections.Generic.List<ScreenSyncMonitor>(),
    };

    public void StartStatic(StaticHeadlessStart body) => SetSync("static");
    public void StartAnimate(AnimateHeadlessStart body) => SetSync("animate");
    public void StartMusic(MusicHeadlessStart body) => SetSync("music");
    public void StartScreen(ScreenHeadlessStart body) => SetSync("screen");
    public void StartGif(GifHeadlessStart body) => SetSync("gif");
    public bool StartMedia(string mediaId)
    {
        _store.Update(s => { s.Lighting.Sync = "media"; s.Lighting.LastMediaId = mediaId; });
        return true;
    }
    public void SetStreaming(SetHeadlessStreaming body) { /* persist later if needed */ }
    public byte[]? CaptureAnimateThumbnail(string key, bool skipCache = false) => null;

    public void UpdateScreenEffect(float hue, float colorize, float saturation, float contrast, bool flipX, bool flipY, bool persist)
    {
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
}
