using Qos.Service.Models.Lighting;

namespace Qos.Service.Lighting;

/// <summary>
/// Aggregate read/write surface for the /lighting/* family. The stub backs
/// state with IConfigStore so the SPA can round-trip every property even
/// without a real RGB engine wired in.
/// </summary>
public interface ILightingProvider
{
    string GetSync();
    void SetSync(string sync);
    void StopAll();
    void SetFrameRate(int frameRate);
    void SetScaleRatio(double ratio);
    void SetBrightness(BrightnessScale scale);
    void SetSpeed(SpeedScale scale);

    AnimateOptions GetAnimateOptions();
    AudioSyncOptions GetAudioSyncOptions();
    ScreenSyncOptions GetScreenSyncOptions();

    void StartStatic(StaticHeadlessStart body);
    void StartAnimate(AnimateHeadlessStart body);
    void StartMusic(MusicHeadlessStart body);
    void StartScreen(ScreenHeadlessStart body);
    void StartGif(GifHeadlessStart body);
    bool StartMedia(string mediaId);
    void SetStreaming(SetHeadlessStreaming body);

    /// <summary>Update the Screen Mirror post-process (hue / colorize / saturation / contrast). Persists to settings when persist=true.</summary>
    void UpdateScreenEffect(float hue, float colorize, float saturation, float contrast, bool persist);
    /// <summary>Update the Media post-process. Same semantics as UpdateScreenEffect.</summary>
    void UpdateMediaEffect(float hue, float colorize, float saturation, float contrast, bool persist);

    /// <summary>Render the given animate effect to a 160x90 BMP for UI previews.</summary>
    byte[]? CaptureAnimateThumbnail(string key, bool skipCache = false);
}
