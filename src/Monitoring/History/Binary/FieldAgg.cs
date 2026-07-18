using System;
using System.Collections.Generic;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// One field's sum/count/max, in real (already unscaled) units - the shared
/// shape every minute-rollup ring (scalar, gpu, fan) reduces its readings to,
/// and the shape a wider decimation step (one that spans several minute
/// slots) folds several of together via <see cref="Combine"/>. Cnt==0 means
/// no reading ever contributed to this field, distinct from a reading whose
/// value was itself zero - Avg and Max are null exactly then, matching
/// MetricSample's "null = source failed" convention at every tier.
/// </summary>
internal readonly record struct FieldAgg(double Sum, int Cnt, double Max)
{
    public static readonly FieldAgg Empty = new(0, 0, 0);

    public double? Avg => Cnt == 0 ? null : Sum / Cnt;
    public double? MaxOrNull => Cnt == 0 ? null : Max;

    /// <summary>Folds another field's aggregate into this one - used both to
    /// combine several minute slots into one wider decimation step, and (via
    /// <see cref="FromReadings"/>) to rebuild a minute slot from its raw
    /// per-second readings.</summary>
    public FieldAgg Combine(FieldAgg other)
    {
        if (Cnt == 0)
        {
            return other;
        }
        if (other.Cnt == 0)
        {
            return this;
        }
        return new FieldAgg(Sum + other.Sum, Cnt + other.Cnt, Math.Max(Max, other.Max));
    }

    /// <summary>Builds one field's aggregate from a sequence of raw readings -
    /// a null reading (source failed that tick) contributes nothing, matching
    /// SQL SUM/MAX/COUNT's own null-skipping behavior over the equivalent raw
    /// column.</summary>
    public static FieldAgg FromReadings(IEnumerable<double?> values)
    {
        double sum = 0;
        var cnt = 0;
        double max = 0;
        foreach (var v in values)
        {
            if (v is not { } value)
            {
                continue;
            }
            sum += value;
            max = cnt == 0 ? value : Math.Max(max, value);
            cnt++;
        }
        return new FieldAgg(sum, cnt, max);
    }
}
