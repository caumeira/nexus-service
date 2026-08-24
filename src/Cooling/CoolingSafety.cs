using System;
using Nexus.Service.Models.Cooling;

namespace Nexus.Service.Cooling;

/// <summary>
/// The fan-write safety boundary - two clamps, defense in depth:
///   • <see cref="ClampDuty(int)"/> bounds every physical write at the composite
///     chokepoint, so no caller (a curve apply, a direct <c>/cooling/fan/{id}/speed</c>
///     call, or - later - a plugin-guided write) can drive a fan outside [0,100].
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

    /// <summary>Bound a curve response time in seconds. Non-finite collapses to 1 s.</summary>
    public static double ClampResponseTime(double seconds) =>
        double.IsFinite(seconds) ? Math.Clamp(seconds, 0.0, 600.0) : 1.0;

    // Non-finite (NaN/Inf) collapses to the safe floor, so a malformed value never
    // persists or round-trips back as NaN.
    public static double ClampDuty(double duty) =>
        double.IsFinite(duty) ? Math.Clamp(duty, MinDuty, MaxDuty) : MinDuty;

    // A speed multiplier (global / per-graph). Non-finite or out-of-range collapses
    // to 1.0 (no-op), never a negative or absurd boost.
    private static double ClampModifier(double modifier) =>
        double.IsFinite(modifier) ? Math.Clamp(modifier, 0.0, MaxGlobalModifier) : 1.0;

    /// <summary>Bound a curve threshold temperature. Non-finite collapses to 0.</summary>
    private static double ClampTemp(double temp) =>
        double.IsFinite(temp) ? Math.Clamp(temp, -273.0, 200.0) : 0.0;

    /// <summary>Clamp every speed in a curve-set body in place and return it.</summary>
    public static SetCurvesBody Sanitize(SetCurvesBody body)
    {
        body.GlobalSpeedModifier = ClampModifier(body.GlobalSpeedModifier);

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
                graph.SpeedModifier = ClampModifier(graph.SpeedModifier);
                foreach (var point in graph.Points)
                {
                    point.Speed = ClampDuty(point.Speed);
                }
            }

            if (curve.Trigger is { } trigger)
            {
                trigger.IdleSpeed = ClampDuty(trigger.IdleSpeed);
                trigger.LoadSpeed = ClampDuty(trigger.LoadSpeed);
                trigger.IdleTemp = ClampTemp(trigger.IdleTemp);
                trigger.LoadTemp = ClampTemp(trigger.LoadTemp);
                // A load threshold at or below the idle one latches the curve
                // to one side forever; keep them a degree apart.
                if (trigger.LoadTemp <= trigger.IdleTemp)
                {
                    trigger.LoadTemp = trigger.IdleTemp + 1;
                }
                trigger.ResponseTime = ClampResponseTime(trigger.ResponseTime);
            }

            if (curve.Sync is { } sync)
            {
                // An offset can subtract as well as add, so it spans the full
                // duty range in both directions; the write still clamps to [0,100].
                sync.Offset = double.IsFinite(sync.Offset) ? Math.Clamp(sync.Offset, -MaxDuty, MaxDuty) : 0;
            }

            if (curve.Auto is { } auto)
            {
                auto.MinSpeed = ClampDuty(auto.MinSpeed);
                auto.MaxSpeed = ClampDuty(auto.MaxSpeed);
                // Reversed speed bounds are what Math.Clamp throws on, and the
                // throw would come from inside the engine tick.
                if (auto.MaxSpeed < auto.MinSpeed)
                {
                    (auto.MinSpeed, auto.MaxSpeed) = (auto.MaxSpeed, auto.MinSpeed);
                }
                auto.IdleTemp = ClampTemp(auto.IdleTemp);
                auto.LoadTemp = ClampTemp(auto.LoadTemp);
                if (auto.LoadTemp <= auto.IdleTemp)
                {
                    auto.LoadTemp = auto.IdleTemp + 1;
                }
                // A zero step would freeze the controller at its start value.
                auto.Step = double.IsFinite(auto.Step) ? Math.Clamp(auto.Step, 0.5, MaxDuty) : 5.0;
                auto.Deadband = double.IsFinite(auto.Deadband) ? Math.Clamp(auto.Deadband, 0.0, 50.0) : 0.0;
                auto.ResponseTime = ClampResponseTime(auto.ResponseTime);
            }
        }

        return body;
    }
}
