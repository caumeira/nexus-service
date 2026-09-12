using System;

namespace Nexus.Service.Peripherals.Hyte.MiniHub;

/// <summary>
/// Agreement filter over one MiniHub port's tach polls (why single polls
/// cannot be trusted: <see cref="MiniHubProtocol.TryParseFanSpeeds"/>).
/// A reading is published only when at least <see cref="MinAgreeing"/> of
/// the last <see cref="WindowSize"/> plausible samples agree within
/// <see cref="Tolerance"/>; otherwise the port reports no RPM.
/// </summary>
public sealed class MiniHubTachConsensus
{
    public const int WindowSize = 20;
    // The captured junk series peak at clusters of 3 within Tolerance.
    public const int MinAgreeing = 5;
    public const int MinPlausibleRpm = 200;
    public const int MaxPlausibleRpm = 4000;
    // A real fan at fixed duty reads bit-identical or one count apart, under
    // 1% at 1000 RPM.
    private const double Tolerance = 0.02;

    private readonly object _lock = new();
    private readonly int[] _window = new int[WindowSize];
    private int _count;
    private int _next;

    public void Add(int rpm)
    {
        lock (_lock)
        {
            _window[_next] = rpm;
            _next = (_next + 1) % WindowSize;
            if (_count < WindowSize) _count++;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _count = 0;
            _next = 0;
        }
    }

    /// <summary>
    /// The agreed RPM, or null when no cluster of <see cref="MinAgreeing"/>
    /// plausible samples exists. The largest cluster wins; its median is
    /// returned so one stray neighbour cannot skew the value.
    /// </summary>
    public int? Evaluate()
    {
        lock (_lock)
        {
            return EvaluateLocked();
        }
    }

    private int? EvaluateLocked()
    {
        var bestCount = 0;
        var bestCenter = 0;
        for (var i = 0; i < _count; i++)
        {
            var center = _window[i];
            if (!IsPlausible(center)) continue;
            var count = 0;
            for (var j = 0; j < _count; j++)
            {
                if (Agrees(center, _window[j])) count++;
            }
            if (count > bestCount)
            {
                bestCount = count;
                bestCenter = center;
            }
        }
        if (bestCount < MinAgreeing) return null;

        Span<int> members = stackalloc int[WindowSize];
        var n = 0;
        for (var j = 0; j < _count; j++)
        {
            if (Agrees(bestCenter, _window[j])) members[n++] = _window[j];
        }
        members[..n].Sort();
        return members[n / 2];
    }

    private static bool IsPlausible(int rpm) => rpm >= MinPlausibleRpm && rpm <= MaxPlausibleRpm;

    private static bool Agrees(int center, int sample)
        => IsPlausible(sample) && Math.Abs(sample - center) <= center * Tolerance;
}
