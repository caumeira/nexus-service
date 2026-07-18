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

    /// <summary>True when the active effect's rendered frame is frozen.</summary>
    bool IsPaused { get; }

    /// <summary>
    /// Freezes or resumes the active effect's rendered frame. A no-op when no
    /// effect is running. Transient - cleared by SetEffect/Stop, never persisted.
    /// </summary>
    void SetPaused(bool paused);
    void SetBrightness(BrightnessScale scale);
    void SetSpeed(SpeedScale scale);

    AnimateOptions GetAnimateOptions();
    AudioSyncOptions GetAudioSyncOptions();
    ScreenSyncOptions GetScreenSyncOptions();

    void StartAnimate(AnimateHeadlessStart body);
    void StartMusic(MusicHeadlessStart body);
    void StartScreen(ScreenHeadlessStart body);
    void ReselectScreen();
    bool StartMedia(string mediaId);
    void StartMediaIdle();

    /// <summary>Activate Game Sync mode and persist the selection.</summary>
    void StartGameSync();

    /// <summary>
    /// Returns the live GameSyncEffect when Game Sync is the active mode,
    /// or null when a different mode is running. Used by the frame receiver
    /// to push inbound shim frames without activating the mode.
    /// </summary>
    Nexus.Service.Lighting.Engine.Effects.GameSyncEffect? ActiveGameSyncEffect();

    /// <summary>Update the Mirror post-process (hue / colorize / saturation / contrast + flip X/Y + reactive). Persists to settings when persist=true.</summary>
    void UpdateScreenEffect(float hue, float colorize, float saturation, float contrast, bool flipX, bool flipY, bool persist, bool reactive = false, float reactivity = 0.5f, float intensity = 0.5f);
    /// <summary>Update the Media post-process. Same semantics as UpdateScreenEffect.</summary>
    void UpdateMediaEffect(float hue, float colorize, float saturation, float contrast, bool flipX, bool flipY, bool persist);

    /// <summary>
    /// Render the given animate effect's preset <paramref name="slot"/> to a
    /// 160x90 BMP for UI previews (presets are universal, so the same render
    /// serves every surface). Returns the BMP bytes plus a content tag (a hash
    /// of the slot's saved look) for the HTTP ETag; the render is re-cached
    /// whenever that tag changes, so an edit is never served stale.
    /// </summary>
    (byte[] Bytes, string Tag)? CaptureAnimateThumbnail(string key, int slot, bool skipCache = false);

    /// <summary>
    /// Persist the universal preset templates and, if the saved change altered
    /// the slot currently driving the LEDs, push the new look to the running
    /// shader in place (so the hardware follows a commit from any surface).
    /// </summary>
    void SaveAnimateTemplates(System.Collections.Generic.Dictionary<string, Nexus.Service.Persistence.AnimateEffectTemplates> templates);

    /// <summary>Persist the MusicReactive flag and reconcile audio capture against the active effect.</summary>
    void SetMusicReactive(bool enabled);

    /// <summary>Start or stop audio capture based on the current MusicReactive setting and active effect.</summary>
    void ReconcileAudioCapture();
}
