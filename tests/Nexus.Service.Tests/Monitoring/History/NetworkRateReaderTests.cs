using Nexus.Service.Monitoring.History;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

public class NetworkRateReaderTests
{
    [Fact]
    public void ComputeRate_ReturnsNull_OnTheFirstRead()
    {
        var rate = NetworkRateReader.ComputeRate(
            lastTicks: -1, lastBytesIn: 0, lastBytesOut: 0,
            nowTicks: 1000, nowBytesIn: 5000, nowBytesOut: 2000);

        Assert.Null(rate.InBytesPerSec);
        Assert.Null(rate.OutBytesPerSec);
    }

    [Fact]
    public void ComputeRate_DividesByteDeltaByElapsedSeconds()
    {
        var rate = NetworkRateReader.ComputeRate(
            lastTicks: 0, lastBytesIn: 1000, lastBytesOut: 500,
            nowTicks: 1000, nowBytesIn: 3000, nowBytesOut: 1500);

        Assert.Equal(2000.0, rate.InBytesPerSec);
        Assert.Equal(1000.0, rate.OutBytesPerSec);
    }

    [Fact]
    public void ComputeRate_ReturnsNull_WhenElapsedExceedsFiveSeconds()
    {
        var rate = NetworkRateReader.ComputeRate(
            lastTicks: 0, lastBytesIn: 1000, lastBytesOut: 500,
            nowTicks: 5001, nowBytesIn: 3000, nowBytesOut: 1500);

        Assert.Null(rate.InBytesPerSec);
        Assert.Null(rate.OutBytesPerSec);
    }

    [Fact]
    public void ComputeRate_ReturnsNull_WhenElapsedIsZeroOrNegative()
    {
        var zero = NetworkRateReader.ComputeRate(
            lastTicks: 1000, lastBytesIn: 0, lastBytesOut: 0,
            nowTicks: 1000, nowBytesIn: 100, nowBytesOut: 100);
        var negative = NetworkRateReader.ComputeRate(
            lastTicks: 2000, lastBytesIn: 0, lastBytesOut: 0,
            nowTicks: 1000, nowBytesIn: 100, nowBytesOut: 100);

        Assert.Null(zero.InBytesPerSec);
        Assert.Null(negative.InBytesPerSec);
    }

    [Fact]
    public void ComputeRate_ReturnsNull_OnANegativeByteDelta()
    {
        // A counter reset (NIC re-enumerated) makes the delta negative.
        var rate = NetworkRateReader.ComputeRate(
            lastTicks: 0, lastBytesIn: 5000, lastBytesOut: 5000,
            nowTicks: 1000, nowBytesIn: 100, nowBytesOut: 100);

        Assert.Null(rate.InBytesPerSec);
        Assert.Null(rate.OutBytesPerSec);
    }

    [Fact]
    public void ComputeRate_AllowsElapsedExactlyFiveSeconds()
    {
        var rate = NetworkRateReader.ComputeRate(
            lastTicks: 0, lastBytesIn: 0, lastBytesOut: 0,
            nowTicks: 5000, nowBytesIn: 5000, nowBytesOut: 0);

        Assert.Equal(1000.0, rate.InBytesPerSec);
    }

    [Fact]
    public void Read_ReturnsNullOnTheFirstCall_ThenAComputedRateOnTheSecond()
    {
        var reader = new NetworkRateReader();

        var first = reader.Read();
        Assert.Null(first.InBytesPerSec);

        var second = reader.Read();
        // Real NIC counters only increase (or stay flat) between two live
        // reads a moment apart, so this asserts the reader produced a
        // non-negative result rather than an exact value.
        Assert.True(second.InBytesPerSec is null || second.InBytesPerSec >= 0);
    }
}
