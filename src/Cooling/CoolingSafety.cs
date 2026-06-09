using System;
using Nexus.Service.Models.Cooling;

namespace Nexus.Service.Cooling;

/// <summary>
/// The fan-write safety boundary — two clamps, defense in depth:
///   • <see cref="ClampDuty(int)"/> bounds every physical write at the composite
///     chokepoint, so no caller (a curve apply, a direct <c>/cooling/fan/{id}/speed</c>
///     call, or — later — a plugin-guided write) can drive a fan outside [0,100].
///   • <see cref="Sanitize"/> bounds the stored curve definition at
///     <c>/cooling/curves/set</c>, so a malformed curve can't persist out-of-range
///     speeds (the apply loop re-clamps the computed output, but bad input
///     shouldn't be stored or shown back).
/// The principle: cooling is written only by the host, and even the host clamps.
/// First-party/global inputs are already in range, so the clamp is a no-op for them.
/// </summary>
public static class CoolingSafety
{
    public const int MinDuty = 0;
    public const int MaxDuty = 100;

    /// <summary>Ceiling for the global boost. The apply loop re-clamps the product
    /// to [0,100]; this just keeps a negative or absurd modifier from persisting.</summary>
    public const double MaxGlobalModifier = 2.0;

    public static int ClampDuty(int dutyPercent) => Math.Clamp(dutyPercent, MinDuty, MaxDuty);

    public static double ClampDuty(double duty) => Math.Clamp(duty, MinDuty, MaxDuty);

    /// <summary>Clamp every speed in a curve-set body in place and return it.</summary>
    public static SetCurvesBody Sanitize(SetCurvesBody body)
    {
        body.GlobalSpeedModifier = double.IsFinite(body.GlobalSpeedModifier)
            ? Math.Clamp(body.GlobalSpeedModifier, 0.0, MaxGlobalModifier)
            : 1.0;

        foreach (var curve in body.Curves)
        {
            if (curve.Flat is { } flat)
            {
                flat.Speed = ClampDuty(flat.Speed);
            }

            if (curve.Linear is { } linear)
            {
                linear.MinSpeed = ClampDuty(linear.MinSpeed);
                linear.MaxSpeed = ClampDuty(linear.MaxSpeed);
            }

            if (curve.Graph is { } graph)
            {
                foreach (var point in graph.Points)
                {
                    point.Speed = ClampDuty(point.Speed);
                }
            }
        }

        return body;
    }
}
