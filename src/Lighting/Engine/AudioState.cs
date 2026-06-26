using System;

namespace Nexus.Service.Lighting.Engine;

/// <summary>
/// Shared latest-frame snapshot of audio features that every shader can read
/// as a uniform. Producer: <see cref="Nexus.Service.Activity.BeatsProvider"/>.
/// Consumers: <see cref="Gpu.ShaderEffect"/>.
///
/// Tiny and allocation-free on both paths: the audio thread writes individual
/// floats, the GL thread reads them. Torn reads are acceptable - these values
/// are noisy and are smoothed again in the shader via per-effect audioBoost
/// scaling.
///
/// When <see cref="Reset"/> is called (music-reactive toggled off, capture
/// stopped, or no device available), every field drops to zero, so every shader
/// sees zero uniforms and its idle animation runs unmodified.
/// </summary>
public static class AudioState
{
    private const int SpectrumBins = 16;

    public static float Level;
    public static float Bass;
    public static float Mid;
    public static float High;
    /// <summary>1.0 when a beat fires; decays exponentially toward 0 over ~0.5s.</summary>
    public static float Beat;
    /// <summary>Log-spaced coarse bands. Length <see cref="SpectrumBins"/>, values in [0, 1].</summary>
    public static readonly float[] Spectrum = new float[SpectrumBins];

    public static int SpectrumLength => SpectrumBins;

    public const int Spectrum64Bins = 64;
    public static readonly float[] Spectrum64 = new float[Spectrum64Bins];
    public static int Spectrum64Length => Spectrum64Bins;

    /// <summary>16-band x 16-frame history packed as 256 floats. Layout: index = frame*16 + band, frame 0 newest.</summary>
    public const int HistFrames = 16;
    public const int HistBands = 16;
    public static readonly float[] SpecHist = new float[HistFrames * HistBands];

    public static float BassPeak;
    public static float MidPeak;
    public static float HighPeak;

    public static void Reset()
    {
        Level = 0;
        Bass = 0;
        Mid = 0;
        High = 0;
        Beat = 0;
        Array.Clear(Spectrum);
        Array.Clear(Spectrum64);
        Array.Clear(SpecHist);
        BassPeak = 0;
        MidPeak = 0;
        HighPeak = 0;
    }
}
