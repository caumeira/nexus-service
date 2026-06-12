using Nexus.Service.Models.Lighting;

namespace Nexus.Service.Lighting;

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
    void SetBrightness(BrightnessScale scale);
    void SetSpeed(SpeedScale scale);

    AnimateOptions GetAnimateOptions();
    AudioSyncOptions GetAudioSyncOptions();
    ScreenSyncOptions GetScreenSyncOptions();

    void StartAnimate(AnimateHeadlessStart body);
    void StartMusic(MusicHeadlessStart body);
    void StartScreen(ScreenHeadlessStart body);
    void ReselectScreen();
    void StartGif(GifHeadlessStart body);
    bool StartMedia(string mediaId);

    /// <summary>Update the Mirror post-process (hue / colorize / saturation / contrast + flip X/Y). Persists to settings when persist=true.</summary>
    void UpdateScreenEffect(float hue, float colorize, float saturation, float contrast, bool flipX, bool flipY, bool persist);
    /// <summary>Update the Media post-process. Same semantics as UpdateScreenEffect.</summary>
    void UpdateMediaEffect(float hue, float colorize, float saturation, float contrast, bool flipX, bool flipY, bool persist);

    /// <summary>
    /// Render the given animate effect to a 160x90 BMP for UI previews. Uses the
    /// user's saved selected-slot look when one exists, else the effect's
    /// signature look. <paramref name="version"/> is an opaque cache-bust token
    /// from the client (changes when the saved look changes); the bytes are
    /// re-rendered whenever it differs from the last render's token.
    /// </summary>
    byte[]? CaptureAnimateThumbnail(string key, string? version = null, bool skipCache = false);
}
