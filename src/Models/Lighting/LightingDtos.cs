using System.Collections.Generic;
using Qos.Service.Models.Common;

namespace Qos.Service.Models.Lighting;

public sealed class AudioStateSnapshot
{
    public float Level { get; set; }
    public float Bass { get; set; }
    public float Mid { get; set; }
    public float High { get; set; }
    public float Beat { get; set; }
    public List<float> Spectrum { get; set; } = new();
}

public sealed class ShaderSourceResponse
{
    public string Frag { get; set; } = "";
}

public class CurrentSyncResponse : ApiResponse
{
    /// <summary>One of: none, static, animate, music, screen, gif.</summary>
    public string Sync { get; set; } = "none";
}

public class SetFrameRateBody { public int FrameRate { get; set; } }
public class SetScaleRatioBody { public double Ratio { get; set; } }

public class BrightnessScale
{
    public Dictionary<string, float> Scale { get; set; } = new();
    public bool Enabled { get; set; }
}

/// <summary>Master multiplier applied to every LED channel before it leaves the
/// RGB bridge. 0..1 (UI slider is 0..100% and divides client-side). Both the
/// /lighting/global-brightness GET response and POST body share this shape.</summary>
public class GlobalBrightnessBody
{
    public float Value { get; set; } = 1.0f;
}

public class SpeedScale
{
    public Dictionary<string, int> Scale { get; set; } = new();
    public bool Enabled { get; set; }
}

public class StaticHeadlessStart { public RGBA Color { get; set; } }

/// <summary>
/// Toggle for the Music Reactive mode. When true, BeatsProvider is started
/// (audio capture + spectrum analysis), so every shader that reads the
/// u_audio* uniforms animates to the music. When false, capture stops and
/// AudioState is zeroed so shaders run their idle animations.
/// </summary>
public sealed class MusicReactiveBody
{
    public bool Enabled { get; set; }
}

/// <summary>Replaces the persisted AnimateSettings.Templates dictionary in one shot.
/// The frontend holds the authoritative set of per-effect templates + selected slot
/// and posts the whole map every time the user clicks a slot or edits one. Backend
/// is a dumb store here - it never constructs templates itself.</summary>
public class SetAnimateTemplatesBody
{
    public Dictionary<string, Qos.Service.Persistence.AnimateEffectTemplates> Templates { get; set; } = new();
}

public class AnimateHeadlessStart
{
    public string Filter { get; set; } = "";
    public float Intensity { get; set; }
    public string Effect { get; set; } = "";
    public int Speed { get; set; }
    public float Noise { get; set; }
    public List<RGBA> Scheme { get; set; } = new();
    public float Hue { get; set; }
    public float Sat { get; set; }
    /// <summary>0 = pure hue shifter, 1 = pure grayscale colorizer, blended in between.</summary>
    public float Colorize { get; set; }
    /// <summary>0 = grayscale, 1 = unchanged, 2 = oversaturated.</summary>
    public float Saturation { get; set; } = 1f;
    /// <summary>0 = flat middle gray, 1 = unchanged, 2 = hard contrast.</summary>
    public float Contrast { get; set; } = 1f;
    /// <summary>Per-effect uniforms keyed by GLSL name (e.g. u_zoom, u_warp).</summary>
    public List<ShaderParam> Params { get; set; } = new();
    /// <summary>
    /// False while the user is actively dragging a slider -- updates the live
    /// engine uniforms but skips the settings.json write. True (default) on
    /// slider release or explicit mode change, which also persists the state.
    /// </summary>
    public bool Persist { get; set; } = true;
}

public class ShaderParam
{
    public string Name { get; set; } = "";
    public float Value { get; set; }
}

public class MusicHeadlessStart
{
    public string Effect { get; set; } = "CircleRamp";
    public string Source { get; set; } = "default";
}

public class ScreenHeadlessStart
{
    public string Monitor { get; set; } = "";
    public string Effect { get; set; } = "";
    public float Saturation { get; set; }
    public float Contrast { get; set; }
    public float Blur { get; set; }
    /// <summary>0..1 hue rotation fraction. Maps to the same palette-ring hue the animate mode uses.</summary>
    public float Hue { get; set; }
    /// <summary>0 = pure hue rotate, 1 = grayscale + accent-tint. Matches the animate colorize semantics.</summary>
    public float Colorize { get; set; }
}

/// <summary>
/// Post-process params applied to Screen Mirror and Media frames after capture /
/// playback. Shared shape so the right-pane Effect tab can drive either mode
/// with one control set.
/// </summary>
public sealed class PostProcessBody
{
    public float Hue { get; set; }
    public float Colorize { get; set; }
    public float Saturation { get; set; } = 1f;
    public float Contrast { get; set; } = 1f;
    public bool FlipX { get; set; }
    public bool FlipY { get; set; }
    /// <summary>False while the user drags a slider. True on release or programmatic change.</summary>
    public bool Persist { get; set; } = true;
}

public class GifHeadlessStart
{
    public int Speed { get; set; }
    public string Mode { get; set; } = "Loop";
    public List<string> Paths { get; set; } = new();
}

public class StreamingScale
{
    public double Width { get; set; }
    public double Height { get; set; }
}

public class Cropper
{
    public double Top { get; set; }
    public double Left { get; set; }
    public double Width { get; set; } = 1000;
    public double Height { get; set; } = 550;
    public string Mirroring { get; set; } = "Normal";
    public string Rotation { get; set; } = "Zero";
}

public class SetHeadlessStreaming
{
    public Dictionary<string, Cropper> Streaming { get; set; } = new();
    public StreamingScale Scale { get; set; } = new();
    public string Mode { get; set; } = "";
}

// On-connect WS payloads
public class AnimateOptions
{
    public string[] Effects { get; set; } = System.Array.Empty<string>();
    public string[] Filters { get; set; } = System.Array.Empty<string>();
}

public class AudioSyncOptions
{
    public string[] Effects { get; set; } = System.Array.Empty<string>();
    public string[] Sources { get; set; } = System.Array.Empty<string>();
}

public class ScreenSyncMonitor
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public class ScreenSyncOptions
{
    public string[] Effects { get; set; } = System.Array.Empty<string>();
    public List<ScreenSyncMonitor> Monitors { get; set; } = new();
}
