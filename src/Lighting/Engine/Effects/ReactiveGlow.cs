using System;

namespace Nexus.Service.Lighting.Engine.Effects;

/// <summary>
/// Analyzes a CanvasBuffer frame (single pass, no allocation) and maintains
/// K smoothed per-band colours for the reactive glow shader. All buffers are
/// pre-allocated in the constructor.
/// </summary>
internal sealed class ReactiveGlow
{
    private const int K = 12;
    private const int HueBins = 36;

    // Pixels below this V are skipped as too dark to contribute meaningful hue.
    private const int DarkGate = 46;

    // Pixels with S*255 below this are treated as near-neutral (white/gray).
    private const int NeutralGate = 64;

    // Ingest accumulators - cleared each Ingest call
    private readonly int[] _hueBins = new int[HueBins];
    private readonly int[] _hueBinsSmoothed = new int[HueBins];
    private readonly int[] _bandHueBins = new int[K * HueBins];
    private readonly int[] _bandV = new int[K];
    private readonly int[] _bandVCount = new int[K];
    private int _prevSceneMeanV;
    private int _neutralWeight;

    // Peak/palette scratch (at most HueBins peaks after find)
    private readonly int[] _peakBins = new int[HueBins];
    private readonly int[] _peakWeights = new int[HueBins];
    private int _peakCount;
    private readonly int[] _paletteBins = new int[HueBins];
    private int _paletteSize;

    // Per-band smoothed state
    private readonly float[] _bandR = new float[K];
    private readonly float[] _bandG = new float[K];
    private readonly float[] _bandB = new float[K];
    private readonly float[] _targetR = new float[K];
    private readonly float[] _targetG = new float[K];
    private readonly float[] _targetB = new float[K];

    // State carried from Ingest to Advance
    private bool _burst;
    private bool _hasSalientPixels;

    /// <summary>
    /// Tightly-packed band colours [r0,g0,b0,r1,g1,b1,...], length K*3.
    /// Uploaded to the shader as uniform vec3[K]. Written by Advance.
    /// </summary>
    public readonly float[] BandFloats = new float[K * 3];

    public int BandCount => K;

    /// <summary>
    /// Single-pass histogram over all canvas pixels. Sets internal targets;
    /// call Advance after each Ingest to smooth toward them.
    /// </summary>
    public void Ingest(CanvasBuffer canvas, float intensity)
    {
        Array.Clear(_hueBins, 0, HueBins);
        Array.Clear(_bandHueBins, 0, K * HueBins);
        Array.Clear(_bandV, 0, K);
        Array.Clear(_bandVCount, 0, K);
        _neutralWeight = 0;

        var px = canvas.Pixels;
        int width = canvas.Width;
        int total = px.Length;
        int sceneMeanVAcc = 0;
        int sceneMeanVCount = 0;

        for (int i = 0; i < total; i += 3)
        {
            int r = px[i];
            int g = px[i + 1];
            int b = px[i + 2];

            int v = r;
            if (g > v)
            {
                v = g;
            }
            if (b > v)
            {
                v = b;
            }

            if (v < DarkGate)
            {
                continue;
            }

            int minVal = r;
            if (g < minVal)
            {
                minVal = g;
            }
            if (b < minVal)
            {
                minVal = b;
            }
            int delta = v - minVal;

            // s_approx is saturation * 255 in integer range
            int sApprox = delta * 255 / (v > 0 ? v : 1);
            if (sApprox < NeutralGate)
            {
                _neutralWeight += v * v;
                sceneMeanVAcc += v;
                sceneMeanVCount++;
                continue;
            }

            if (delta == 0)
            {
                continue;
            }

            // Integer HSV hue discretized to HueBins bins; each 60-degree
            // sextant covers 6 bins. Per-branch: hue*36 = sextant offset +
            // (mid-max - other)*6/delta.
            int bin;
            if (v == r)
            {
                // hue * 36 = (g - b) * 6 / delta, range approx -6..6, wraps at boundaries
                int raw6 = (g - b) * 6 / delta;
                bin = ((raw6 % HueBins) + HueBins) % HueBins;
            }
            else if (v == g)
            {
                // hue * 36 = 12 + (b - r) * 6 / delta
                int raw6 = (b - r) * 6 / delta + 12;
                bin = ((raw6 % HueBins) + HueBins) % HueBins;
            }
            else
            {
                // hue * 36 = 24 + (r - g) * 6 / delta
                int raw6 = (r - g) * 6 / delta + 24;
                bin = ((raw6 % HueBins) + HueBins) % HueBins;
            }

            // Weight: biases toward vivid, bright pixels (sv^2 / 255)
            int sv = sApprox * v / 255;
            int w = sv * sv / 255;
            _hueBins[bin] += w;

            int pixIdx = i / 3;
            int x = pixIdx % width;
            int band = x * K / width;
            _bandHueBins[band * HueBins + bin] += w;
            _bandV[band] += v;
            _bandVCount[band]++;
            sceneMeanVAcc += v;
            sceneMeanVCount++;
        }

        // [1,2,1] kernel smooths the hue histogram to suppress narrow spikes
        for (int i = 0; i < HueBins; i++)
        {
            int prev = (i + HueBins - 1) % HueBins;
            int next = (i + 1) % HueBins;
            _hueBinsSmoothed[i] = (_hueBins[prev] + _hueBins[i] * 2 + _hueBins[next]) / 4;
        }

        // Find local-maximum peaks
        _peakCount = 0;
        for (int i = 0; i < HueBins; i++)
        {
            int prev = (i + HueBins - 1) % HueBins;
            int next = (i + 1) % HueBins;
            if (_hueBinsSmoothed[i] > _hueBinsSmoothed[prev]
                && _hueBinsSmoothed[i] > _hueBinsSmoothed[next]
                && _hueBinsSmoothed[i] > 0)
            {
                _peakBins[_peakCount] = i;
                _peakWeights[_peakCount] = _hueBinsSmoothed[i];
                _peakCount++;
            }
        }

        // Merge peaks that are within 3 bins of each other (30 degrees)
        bool merged = true;
        while (merged && _peakCount > 1)
        {
            merged = false;
            for (int a = 0; a < _peakCount && !merged; a++)
            {
                for (int bi = a + 1; bi < _peakCount && !merged; bi++)
                {
                    int diff = Math.Abs(_peakBins[a] - _peakBins[bi]);
                    if (diff > HueBins / 2)
                    {
                        diff = HueBins - diff;
                    }
                    if (diff <= 3)
                    {
                        int remove = _peakWeights[a] >= _peakWeights[bi] ? bi : a;
                        _peakBins[remove] = _peakBins[_peakCount - 1];
                        _peakWeights[remove] = _peakWeights[_peakCount - 1];
                        _peakCount--;
                        merged = true;
                    }
                }
            }
        }

        // Sort peaks descending by weight (selection sort; at most HueBins peaks)
        for (int a = 0; a < _peakCount - 1; a++)
        {
            int maxIdx = a;
            for (int bi = a + 1; bi < _peakCount; bi++)
            {
                if (_peakWeights[bi] > _peakWeights[maxIdx])
                {
                    maxIdx = bi;
                }
            }
            if (maxIdx != a)
            {
                int tmpBin = _peakBins[a]; _peakBins[a] = _peakBins[maxIdx]; _peakBins[maxIdx] = tmpBin;
                int tmpW = _peakWeights[a]; _peakWeights[a] = _peakWeights[maxIdx]; _peakWeights[maxIdx] = tmpW;
            }
        }

        // Total salient (non-neutral) weight for dominance gate
        int totalSalientWeight = 0;
        for (int i = 0; i < HueBins; i++)
        {
            totalSalientWeight += _hueBinsSmoothed[i];
        }

        // Palette size: normally all peaks; collapse to 1 if top peak dominates >= 70%
        int paletteSize = _peakCount;
        if (_peakCount > 1 && totalSalientWeight > 0 && _peakWeights[0] * 10 >= totalSalientWeight * 7)
        {
            paletteSize = 1;
        }
        // Intensity caps the number of distinct colours shown (0 -> 1, 1 -> 5).
        // Applied here, before the per-band targets are computed, so the cap
        // actually constrains the palette the bands draw from.
        int nCap = Math.Max(1, (int)MathF.Round(1f + intensity * 4f));
        if (paletteSize > nCap)
        {
            paletteSize = nCap;
        }
        _paletteSize = paletteSize;
        for (int i = 0; i < _paletteSize; i++)
        {
            _paletteBins[i] = _peakBins[i];
        }

        // Scene-cut: abrupt brightness drop resets band state to black
        int sceneMeanV = sceneMeanVCount > 0 ? sceneMeanVAcc / sceneMeanVCount : 0;
        bool burst = sceneMeanV > _prevSceneMeanV + 102;
        bool sceneCut = sceneMeanV < 30 && _prevSceneMeanV > 200;
        _prevSceneMeanV = sceneMeanV;
        _burst = burst;

        if (sceneCut)
        {
            Array.Clear(_bandR, 0, K);
            Array.Clear(_bandG, 0, K);
            Array.Clear(_bandB, 0, K);
        }

        _hasSalientPixels = totalSalientWeight > 0;

        if (_hasSalientPixels && _paletteSize > 0)
        {
            for (int band = 0; band < K; band++)
            {
                int meanV = _bandVCount[band] > 0 ? _bandV[band] / _bandVCount[band] : 0;

                // Pick the palette colour this band votes for most (3-bin neighbourhood)
                int bestPaletteIdx = 0;
                int bestVote = -1;
                for (int pi = 0; pi < _paletteSize; pi++)
                {
                    int palBin = _paletteBins[pi];
                    int votes = 0;
                    for (int di = -2; di <= 2; di++)
                    {
                        int checkBin = ((palBin + di) % HueBins + HueBins) % HueBins;
                        votes += _bandHueBins[band * HueBins + checkBin];
                    }
                    if (votes > bestVote)
                    {
                        bestVote = votes;
                        bestPaletteIdx = pi;
                    }
                }

                float hue = _paletteBins[bestPaletteIdx] / (float)HueBins;
                float scale = meanV / 255f;
                HsvToRgb(hue, 1f, scale, out float tr, out float tg, out float tb);
                _targetR[band] = tr;
                _targetG[band] = tg;
                _targetB[band] = tb;
            }
        }
    }

    /// <summary>
    /// Applies EMA smoothing toward targets and writes BandFloats.
    /// Call once per frame after Ingest.
    /// </summary>
    public void Advance(float reactivity)
    {
        float attack = 0.08f + reactivity * 0.42f;
        float decay = 0.02f + reactivity * 0.13f;
        if (_burst)
        {
            attack = 0.9f;
        }

        for (int band = 0; band < K; band++)
        {
            if (!_hasSalientPixels)
            {
                _bandR[band] *= 0.98f;
                _bandG[band] *= 0.98f;
                _bandB[band] *= 0.98f;
            }
            else
            {
                float dr = _targetR[band] - _bandR[band];
                float dg = _targetG[band] - _bandG[band];
                float db = _targetB[band] - _bandB[band];
                _bandR[band] += (dr > 0 ? attack : decay) * dr;
                _bandG[band] += (dg > 0 ? attack : decay) * dg;
                _bandB[band] += (db > 0 ? attack : decay) * db;
            }

            BandFloats[band * 3] = _bandR[band];
            BandFloats[band * 3 + 1] = _bandG[band];
            BandFloats[band * 3 + 2] = _bandB[band];
        }
    }

    private static void HsvToRgb(float h, float s, float v, out float r, out float g, out float b)
    {
        h = h - (float)Math.Floor(h);
        float c = v * s;
        float hp = h * 6f;
        float x = c * (1f - Math.Abs((hp % 2f) - 1f));
        float m = v - c;
        float r1 = 0f, g1 = 0f, b1 = 0f;
        if (hp < 1f)
        {
            r1 = c; g1 = x;
        }
        else if (hp < 2f)
        {
            r1 = x; g1 = c;
        }
        else if (hp < 3f)
        {
            g1 = c; b1 = x;
        }
        else if (hp < 4f)
        {
            g1 = x; b1 = c;
        }
        else if (hp < 5f)
        {
            r1 = x; b1 = c;
        }
        else
        {
            r1 = c; b1 = x;
        }
        r = r1 + m;
        g = g1 + m;
        b = b1 + m;
    }
}
