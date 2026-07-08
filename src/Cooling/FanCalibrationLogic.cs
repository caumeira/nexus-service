using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Cooling;

namespace Nexus.Service.Cooling;

/// <summary>
/// Pure classification and stability detection logic for fan calibration.
/// Platform-independent, no LHM dependency. Used by FanCalibrator (Windows)
/// and tests.
/// </summary>
public static class FanCalibrationLogic
{
    public static bool IsStable(IEnumerable<int> samples)
    {
        var arr = samples.Select(s => (double)s).ToArray();
        if (arr.Length == 0)
        {
            return true;
        }

        var mean = arr.Average();
        var variance = arr.Select(x => (x - mean) * (x - mean)).Sum() / arr.Length;
        var stddev = Math.Sqrt(variance);
        var tolerance = Math.Max(20.0, mean * 0.03);
        return stddev < tolerance;
    }

    /// <summary>
    /// A slow monotonic ramp has low variance over a short window while still
    /// far from terminal RPM, so IsStable alone latches mid-ramp. This also
    /// requires the first-half and second-half window means to agree, which
    /// rejects a trending window and waits for the true plateau.
    /// </summary>
    public static bool IsSettled(IReadOnlyList<int> samples)
    {
        if (!IsStable(samples))
        {
            return false;
        }

        if (samples.Count < 2)
        {
            return true;
        }

        var half = samples.Count / 2;
        var firstMean = samples.Take(half).Average();
        var secondMean = samples.Skip(samples.Count - half).Average();
        var mean = samples.Average();
        var tol = Math.Max(15.0, mean * 0.02);
        return Math.Abs(secondMean - firstMean) <= tol;
    }

    public static FanCalibration Classify(string fanId, List<FanCalibrationPoint> curve)
    {
        var rpms = curve.Select(p => p.Rpm).ToList();
        var maxRpm = rpms.Count > 0 ? rpms.Max() : 0;
        // MinRpm is the true floor - 0 when the fan stops at low duty - so the
        // reported range reflects that it stalls. The lowest duty that still
        // spins the fan (its controllable floor) is kept separately in MinDuty.
        var minRpmAll = rpms.Count > 0 ? rpms.Min() : 0;
        var nonZero = curve.Where(p => p.Rpm > 0).ToList();
        var minDuty = nonZero.Count > 0 ? nonZero.Min(p => p.Duty) : 0;

        string classification;
        if (maxRpm <= 100)
        {
            classification = "Unresponsive";
        }
        else if (maxRpm - minRpmAll <= 200)
        {
            classification = "Fixed";
        }
        else if (rpms.Any(r => r == 0) && maxRpm > 100)
        {
            classification = "Stalling";
        }
        else
        {
            classification = "Controllable";
        }

        return new FanCalibration
        {
            FanId = fanId,
            Classification = classification,
            MinRpm = minRpmAll,
            MaxRpm = maxRpm,
            MinDuty = minDuty,
            Curve = curve,
            CalibratedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }
}
