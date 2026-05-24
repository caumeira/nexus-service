using System;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

/// <summary>
/// Shared beat detection + FFT-based spectrum analysis used by every audio
/// capture source (<see cref="BeatsProvider"/> on macOS / Linux via ffmpeg,
/// <see cref="WasapiLoopbackBeatsProvider"/> on Windows via WASAPI loopback).
///
/// Call <see cref="Analyse"/> with a mono float32 window of exactly
/// <see cref="WindowSize"/> samples at <see cref="SampleRate"/>. Returns the
/// legacy <see cref="MusicResult"/> for the existing /api/music beats WS
/// topic and also publishes richer audio features (level / bass / mid / high
/// / beat / 16-band spectrum) to <see cref="AudioState"/> so shaders can read
/// them as uniforms.
///
/// State (rolling averages, smoothed spectrum, beat envelope) is kept on this
/// instance. One analyser per capture source - they should not be shared
/// across threads without external locking.
/// </summary>
public sealed class AudioAnalyser
{
    public const int SampleRate = 44100;
    public const int WindowSize = 2048;
    private const float BeatThreshold = 1.4f;

    private const int FftSize = 512;
    private const int FftBits = 9;
    private const int FftBins = FftSize / 2;

    private static readonly int[] _bandEdges =
    {
        1, 3, 5, 7, 10, 14, 19, 26, 34, 45, 59, 76, 97, 125, 156, 196, FftBins,
    };

    private static readonly float[] _cosTable = new float[FftSize];
    private static readonly float[] _sinTable = new float[FftSize];
    private static readonly int[] _bitReverse = new int[FftSize];
    private static readonly float[] _hannWindow = new float[FftSize];

    static AudioAnalyser()
    {
        for (int i = 0; i < FftSize; i++)
        {
            float ang = -2f * MathF.PI * i / FftSize;
            _cosTable[i] = MathF.Cos(ang);
            _sinTable[i] = MathF.Sin(ang);
            int j = 0, k = i;
            for (int b = 0; b < FftBits; b++)
            {
                j = (j << 1) | (k & 1);
                k >>= 1;
            }
            _bitReverse[i] = j;
            _hannWindow[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (FftSize - 1)));
        }
    }

    // Rolling exponential averages per band - used by the legacy
    // time-domain beat detector.
    private float _avgBass;
    private float _avgSnare;
    private float _avgHigh;
    private int _bassBeatCount;
    private int _snareBeatCount;
    private int _highBeatCount;

    private readonly float[] _fftRe = new float[FftSize];
    private readonly float[] _fftIm = new float[FftSize];
    private readonly float[] _smoothSpectrum = new float[16];
    private float _smoothLevel;
    private float _smoothBass;
    private float _smoothMid;
    private float _smoothHigh;
    private float _beatEnvelope;

    /// <summary>
    /// Analyse one window of mono float32 samples.
    /// </summary>
    /// <param name="samples">Mono float32 PCM, length must equal <see cref="WindowSize"/>.</param>
    /// <param name="externalPeak">
    /// Optional post-system-volume peak in [0, 1]. Windows passes the WASAPI
    /// IAudioMeterInformation peak so volume tracks the same calibrated meter
    /// the OS shows the user (and survives loopback going silent). When null,
    /// volume falls back to the in-window peak of <paramref name="samples"/>.
    /// </param>
    public MusicResult Analyse(float[] samples, float? externalPeak = null)
    {
        float bassEnergy = 0;
        float snareEnergy = 0;
        float highEnergy = 0;
        float internalPeak = 0;

        for (int i = 1; i < samples.Length; i++)
        {
            float s = samples[i];
            float prev = samples[i - 1];
            float abs = MathF.Abs(s);
            float delta = MathF.Abs(s - prev);
            if (abs > internalPeak) internalPeak = abs;
            if (abs > 0.05f && delta < abs * 0.5f)
            {
                bassEnergy += abs;
            }
            snareEnergy += delta * delta;
            if ((s >= 0 && prev < 0) || (s < 0 && prev >= 0))
            {
                highEnergy += delta;
            }
        }

        float n = samples.Length;
        bassEnergy /= n;
        snareEnergy /= n;
        highEnergy /= n;

        const float alpha = 0.1f;
        _avgBass = _avgBass * (1 - alpha) + bassEnergy * alpha;
        _avgSnare = _avgSnare * (1 - alpha) + snareEnergy * alpha;
        _avgHigh = _avgHigh * (1 - alpha) + highEnergy * alpha;

        bool bassBeat = _avgBass > 0.0001f && bassEnergy / _avgBass > BeatThreshold;
        bool snareBeat = _avgSnare > 0.0001f && snareEnergy / _avgSnare > BeatThreshold;
        bool highBeat = _avgHigh > 0.0001f && highEnergy / _avgHigh > BeatThreshold;

        if (bassBeat)
            _bassBeatCount++;
        if (snareBeat)
            _snareBeatCount++;
        if (highBeat)
            _highBeatCount++;

        // Calibrated peak, then perceptual curve so quiet content still moves
        // shaders and loud content doesn't pin at 1.0. Multiplier slightly
        // above 1 lets the saturation hit cleanly when peak == 1.
        float peak = externalPeak ?? internalPeak;
        if (peak < 0f) peak = 0f;
        else if (peak > 1f) peak = 1f;
        float volume = peak <= 0f ? 0f : MathF.Min(MathF.Pow(peak, 0.15f) * 1.003f, 1f);

        float dynamic = MathF.Min(
            (bassEnergy / MathF.Max(_avgBass, 0.0001f) +
             snareEnergy / MathF.Max(_avgSnare, 0.0001f) +
             highEnergy / MathF.Max(_avgHigh, 0.0001f)) / 3f / BeatThreshold,
            1f);

        PublishAudioState(samples, volume, bassBeat);

        return new MusicResult
        {
            BassBeatCount = _bassBeatCount,
            BassIntensity = MathF.Min(bassEnergy / MathF.Max(_avgBass, 0.0001f) / BeatThreshold, 1f),
            SnareBeatCount = _snareBeatCount,
            SnareIntensity = MathF.Min(snareEnergy / MathF.Max(_avgSnare, 0.0001f) / BeatThreshold, 1f),
            HighBeatCount = _highBeatCount,
            HighIntensity = MathF.Min(highEnergy / MathF.Max(_avgHigh, 0.0001f) / BeatThreshold, 1f),
            KickBeatCount = _bassBeatCount,
            KickIntensity = MathF.Min(bassEnergy / MathF.Max(_avgBass, 0.0001f) / BeatThreshold, 1f),
            MusicDynamic = dynamic,
            Volume = volume,
        };
    }

    public void Reset()
    {
        _avgBass = _avgSnare = _avgHigh = 0;
        _smoothLevel = _smoothBass = _smoothMid = _smoothHigh = _beatEnvelope = 0;
        Array.Clear(_smoothSpectrum);
        AudioState.Reset();
    }

    private void PublishAudioState(float[] samples, float volume, bool bassBeat)
    {
        for (int i = 0; i < FftSize; i++)
        {
            _fftRe[i] = samples[i] * _hannWindow[i];
            _fftIm[i] = 0f;
        }
        Fft(_fftRe, _fftIm);

        Span<float> raw = stackalloc float[16];
        for (int b = 0; b < 16; b++)
        {
            int start = _bandEdges[b];
            int end = _bandEdges[b + 1];
            float sum = 0f;
            for (int k = start; k < end; k++)
            {
                float re = _fftRe[k];
                float im = _fftIm[k];
                sum += MathF.Sqrt(re * re + im * im);
            }
            float avg = sum / (end - start);
            // log1p compression hits its knee around avg=5 which matches the
            // FFT-magnitude range we see for normal-volume music playback.
            // Without the extra gain, individual bands never reach the 0.3+
            // range that makes the spectrum shaders visibly dance.
            float norm = MathF.Log10(1f + avg * 3f);
            raw[b] = MathF.Min(norm, 1f);
        }

        for (int b = 0; b < 16; b++)
        {
            float cur = _smoothSpectrum[b];
            float next = raw[b] > cur
                ? cur + (raw[b] - cur) * 0.55f
                : cur + (raw[b] - cur) * 0.18f;
            _smoothSpectrum[b] = next;
            AudioState.Spectrum[b] = next;
        }

        float bassAgg = (_smoothSpectrum[0] + _smoothSpectrum[1] + _smoothSpectrum[2]) / 3f;
        float midAgg = (_smoothSpectrum[3] + _smoothSpectrum[4] + _smoothSpectrum[5] +
                        _smoothSpectrum[6] + _smoothSpectrum[7] + _smoothSpectrum[8]) / 6f;
        float highAgg = (_smoothSpectrum[9] + _smoothSpectrum[10] + _smoothSpectrum[11] +
                         _smoothSpectrum[12] + _smoothSpectrum[13] + _smoothSpectrum[14] +
                         _smoothSpectrum[15]) / 7f;

        _smoothLevel = _smoothLevel + (volume - _smoothLevel) * 0.25f;
        _smoothBass = _smoothBass + (bassAgg - _smoothBass) * 0.35f;
        _smoothMid = _smoothMid + (midAgg - _smoothMid) * 0.3f;
        _smoothHigh = _smoothHigh + (highAgg - _smoothHigh) * 0.3f;

        _beatEnvelope *= 0.78f;
        if (bassBeat)
            _beatEnvelope = 1f;

        AudioState.Level = _smoothLevel;
        AudioState.Bass = _smoothBass;
        AudioState.Mid = _smoothMid;
        AudioState.High = _smoothHigh;
        AudioState.Beat = _beatEnvelope;
    }

    private static void Fft(float[] re, float[] im)
    {
        for (int i = 0; i < FftSize; i++)
        {
            int j = _bitReverse[i];
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= FftSize; len <<= 1)
        {
            int half = len >> 1;
            int step = FftSize / len;
            for (int i = 0; i < FftSize; i += len)
            {
                int twiddleIdx = 0;
                for (int k = 0; k < half; k++)
                {
                    int idx = i + k;
                    int idx2 = idx + half;
                    float wRe = _cosTable[twiddleIdx];
                    float wIm = _sinTable[twiddleIdx];
                    float tRe = re[idx2] * wRe - im[idx2] * wIm;
                    float tIm = re[idx2] * wIm + im[idx2] * wRe;
                    re[idx2] = re[idx] - tRe;
                    im[idx2] = im[idx] - tIm;
                    re[idx] += tRe;
                    im[idx] += tIm;
                    twiddleIdx += step;
                }
            }
        }
    }
}
