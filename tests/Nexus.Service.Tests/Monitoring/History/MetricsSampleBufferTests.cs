using System;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

public class MetricsSampleBufferTests
{
    private static MetricSample Sample(long ts, double cpu = 50) =>
        new(ts, cpu, null, null, null, null, Array.Empty<GpuReading>(), Array.Empty<FanReading>());

    [Fact]
    public void Append_SameTimestamp_ReplacesRatherThanDuplicates()
    {
        var buffer = new MetricsSampleBuffer();

        buffer.Append(Sample(1000, cpu: 10));
        buffer.Append(Sample(1000, cpu: 90));

        var pending = buffer.PendingSnapshot();
        var only = Assert.Single(pending);
        Assert.Equal(90, only.CpuPercent);
    }

    [Fact]
    public void PendingSnapshot_ReturnsOldestFirst()
    {
        var buffer = new MetricsSampleBuffer();
        buffer.Append(Sample(3000));
        buffer.Append(Sample(1000));
        buffer.Append(Sample(2000));

        var pending = buffer.PendingSnapshot();

        Assert.Equal(new long[] { 1000, 2000, 3000 }, pending.Select(s => s.TsSec).ToArray());
    }

    [Fact]
    public void SnapshotRange_ExcludesSamplesOutsideRange()
    {
        var buffer = new MetricsSampleBuffer();
        buffer.Append(Sample(1000));
        buffer.Append(Sample(2000));
        buffer.Append(Sample(3000));

        var range = buffer.SnapshotRange(1500, 2500);

        var only = Assert.Single(range);
        Assert.Equal(2000, only.TsSec);
    }

    [Fact]
    public void RemoveThrough_DropsSamplesAtOrBeforeTheGivenTs()
    {
        var buffer = new MetricsSampleBuffer();
        buffer.Append(Sample(1000));
        buffer.Append(Sample(2000));
        buffer.Append(Sample(3000));

        buffer.RemoveThrough(2000);

        var pending = buffer.PendingSnapshot();
        var only = Assert.Single(pending);
        Assert.Equal(3000, only.TsSec);
    }

    [Fact]
    public void TrimToRetentionCap_DropsSamplesOlderThanTenMinutes()
    {
        var buffer = new MetricsSampleBuffer();
        var now = 100_000L;
        buffer.Append(Sample(now - 700)); // older than the 600s cap
        buffer.Append(Sample(now - 100)); // within the cap

        buffer.TrimToRetentionCap(now);

        var pending = buffer.PendingSnapshot();
        var only = Assert.Single(pending);
        Assert.Equal(now - 100, only.TsSec);
    }

    [Fact]
    public void TrimToRetentionCap_KeepsExactlyTenMinutesOfSamples()
    {
        var buffer = new MetricsSampleBuffer();
        var now = 100_000L;
        buffer.Append(Sample(now - 600)); // exactly at the cap boundary: kept

        buffer.TrimToRetentionCap(now);

        Assert.Single(buffer.PendingSnapshot());
    }
}
