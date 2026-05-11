using System;
using System.Collections.Generic;
using System.Linq;
using Qos.Service.Models.Cooling;

namespace Qos.Service.Cooling;

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

    public static FanCalibration Classify(string fanId, List<FanCalibrationPoint> curve)
    {
        var rpms = curve.Select(p => p.Rpm).ToList();
        var maxRpm = rpms.Count > 0 ? rpms.Max() : 0;
        var minRpmAll = rpms.Count > 0 ? rpms.Min() : 0;
        var nonZero = curve.Where(p => p.Rpm > 0).ToList();
        var minRpm = nonZero.Count > 0 ? nonZero.Min(p => p.Rpm) : 0;
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
            MinRpm = minRpm,
            MaxRpm = maxRpm,
            MinDuty = minDuty,
            Curve = curve,
            CalibratedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }
}
