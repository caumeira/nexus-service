using System;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Shared x10 fixed-point and plain-whole-int codecs into an i16, each with
/// its own reserved "null" sentinel and clamp-rather-than-wrap overflow
/// handling. Used by the scalar minute rollup ring's x10 max fields and the
/// gpu/fan entity rings' per-second and per-minute-max fields alike.
/// ScalarRingStore keeps its own copy of the x10 codec predating this file
/// (its per-second fields are a mix of i16 and i64 widths, so switching it to
/// share this file would not remove much duplication and risks a working,
/// already-reviewed file for little gain).
/// </summary>
internal static class FixedPointCodec
{
    private const short NullX10 = short.MinValue;
    private const short NullInt16 = short.MinValue;

    // Non-finite (NaN/Infinity) is treated the same as a missing reading
    // rather than let a bad sensor value survive the cast below with an
    // implementation-defined result.
    public static short ScaleX10(double? value)
    {
        if (value is not { } v || !double.IsFinite(v))
        {
            return NullX10;
        }
        var scaled = Math.Round(v * 10);
        if (scaled <= short.MinValue)
        {
            return short.MinValue + 1; // MinValue itself is reserved for null
        }
        if (scaled >= short.MaxValue)
        {
            return short.MaxValue;
        }
        return (short)scaled;
    }

    public static double? UnscaleX10(short raw) => raw == NullX10 ? null : raw / 10.0;

    public static short ScaleInt16(int? value)
    {
        if (value is not { } v)
        {
            return NullInt16;
        }
        if (v <= short.MinValue)
        {
            return short.MinValue + 1; // MinValue itself is reserved for null
        }
        if (v >= short.MaxValue)
        {
            return short.MaxValue;
        }
        return (short)v;
    }

    public static int? UnscaleInt16(short raw) => raw == NullInt16 ? null : raw;
}
