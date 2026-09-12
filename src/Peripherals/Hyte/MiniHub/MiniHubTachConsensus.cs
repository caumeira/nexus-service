using System;

namespace Nexus.Service.Peripherals.Hyte.MiniHub;

/// <summary>
/// Agreement filter over one MiniHub port's tach polls. The hub's period
/// byte is corrupted whenever the fan PWM line is toggling (any duty below
/// 100%, and always in motherboard mode), so single polls swing between the
/// true speed and junk. A reading is published only when at least
/// <see cref="MinAgreeing"/> of the last <see cref="WindowSize"/> plausible
/// samples agree within 2%; otherwise the port reports
/// no RPM rather than a number.
/// </summary>
public sealed class MiniHubTachConsensus
{
    /// <summary>Polls kept; 40 s at the 2 s heartbeat.</summary>
    public const int WindowSize = 20;
    /// <summary>Samples that must agree before a value is published (~8 s of the same reading).</summary>
    public const int MinAgreeing = 4;
    public const int MinPlausibleRpm = 200;
    public const int MaxPlausibleRpm = 4000;
    // A real fan at fixed duty reads bit-identical or one count apart (under
    // 1% at 1000 RPM); junk clusters loosely (4% apart), so 2% separates them.
    private const double Tolerance = 0.02;

    private readonly int[] _window = new int[WindowSize];
    private int _count;
    private int _next;

    public void Add(int rpm)
    {
        _window[_next] = rpm;
        _next = (_next + 1) % WindowSize;
        if (_count < WindowSize) _count++;
    }

    public void Reset()
    {
        _count = 0;
        _next = 0;
    }

    /// <summary>
    /// The agreed RPM, or null when no cluster of <see cref="MinAgreeing"/>
    /// plausible samples exists. The largest cluster wins; its median is
    /// returned so one stray neighbour cannot skew the value.
    /// </summary>
    public int? Evaluate()
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
