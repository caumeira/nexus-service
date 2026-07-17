using System.Linq;
using Nexus.Service.Monitoring.History;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

public class AppSampleBufferTests
{
    private static AppUsageTick Tick(long ts, double cpuValue = 10) =>
        new(ts, new[] { new AppMetricSample("cpu", new[] { new AppUsagePoint("app.exe", cpuValue, null) }) });

    [Fact]
    public void Append_SameTimestamp_ReplacesRatherThanDuplicates()
    {
        var buffer = new AppSampleBuffer();

        buffer.Append(Tick(1000, cpuValue: 10));
        buffer.Append(Tick(1000, cpuValue: 90));

        var pending = buffer.PendingSnapshot();
        var only = Assert.Single(pending);
        Assert.Equal(90, only.Metrics.Single().Apps.Single().Value);
    }

    [Fact]
    public void PendingSnapshot_ReturnsOldestFirst()
    {
        var buffer = new AppSampleBuffer();
        buffer.Append(Tick(3000));
        buffer.Append(Tick(1000));
        buffer.Append(Tick(2000));

        var pending = buffer.PendingSnapshot();

        Assert.Equal(new long[] { 1000, 2000, 3000 }, pending.Select(s => s.TsSec).ToArray());
    }

    [Fact]
    public void SnapshotRange_ExcludesTicksOutsideRange()
    {
        var buffer = new AppSampleBuffer();
        buffer.Append(Tick(1000));
        buffer.Append(Tick(2000));
        buffer.Append(Tick(3000));

        var range = buffer.SnapshotRange(1500, 2500);

        var only = Assert.Single(range);
        Assert.Equal(2000, only.TsSec);
    }

    [Fact]
    public void RemoveThrough_DropsTicksAtOrBeforeTheGivenTs()
    {
        var buffer = new AppSampleBuffer();
        buffer.Append(Tick(1000));
        buffer.Append(Tick(2000));
        buffer.Append(Tick(3000));

        buffer.RemoveThrough(2000);

        var pending = buffer.PendingSnapshot();
        var only = Assert.Single(pending);
        Assert.Equal(3000, only.TsSec);
    }

    [Fact]
    public void TrimToRetentionCap_DropsTicksOlderThanTenMinutes()
    {
        var buffer = new AppSampleBuffer();
        var now = 100_000L;
        buffer.Append(Tick(now - 700)); // older than the 600s cap
        buffer.Append(Tick(now - 100)); // within the cap

        buffer.TrimToRetentionCap(now);

        var pending = buffer.PendingSnapshot();
        var only = Assert.Single(pending);
        Assert.Equal(now - 100, only.TsSec);
    }

    [Fact]
    public void TrimToRetentionCap_KeepsExactlyTenMinutesOfTicks()
    {
        var buffer = new AppSampleBuffer();
        var now = 100_000L;
        buffer.Append(Tick(now - 600)); // exactly at the cap boundary: kept

        buffer.TrimToRetentionCap(now);

        Assert.Single(buffer.PendingSnapshot());
    }
}
