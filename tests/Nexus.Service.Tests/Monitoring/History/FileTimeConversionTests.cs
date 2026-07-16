using Nexus.Service.Monitoring.History;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

public class FileTimeConversionTests
{
    // Real ConsentStore sample captured on T1: LastUsedTimeStart/Stop for one
    // session, ~13.4 minutes apart on 2025-11-20 (verified independently
    // against the FILETIME epoch definition before pinning as a test value).
    private const long SampleStartFileTime = 134080926788115536;
    private const long SampleStopFileTime = 134080934834896115;

    [Fact]
    public void ToUnixSeconds_ConvertsTheSampleStart()
    {
        Assert.Equal(1763619078L, FileTimeConversion.ToUnixSeconds(SampleStartFileTime));
    }

    [Fact]
    public void ToUnixSeconds_ConvertsTheSampleStop()
    {
        Assert.Equal(1763619883L, FileTimeConversion.ToUnixSeconds(SampleStopFileTime));
    }

    [Fact]
    public void ToUnixSeconds_PreservesTheRealDurationBetweenStartAndStop()
    {
        var start = FileTimeConversion.ToUnixSeconds(SampleStartFileTime);
        var stop = FileTimeConversion.ToUnixSeconds(SampleStopFileTime);

        Assert.Equal(805L, stop - start);
    }

    [Fact]
    public void ToUnixSeconds_ReturnsNull_ForZero()
    {
        // LastUsedTimeStop == 0 means "still in use", not epoch zero.
        Assert.Null(FileTimeConversion.ToUnixSeconds(0));
    }

    [Fact]
    public void ToUnixSeconds_ReturnsNull_ForNegativeValues()
    {
        Assert.Null(FileTimeConversion.ToUnixSeconds(-1));
    }

    [Fact]
    public void ToUnixSeconds_TruncatesTowardZero_ForATimeBeforeTheUnixEpoch()
    {
        Assert.Equal(-11_644_473_599L, FileTimeConversion.ToUnixSeconds(1));
    }

    [Fact]
    public void ToUnixSeconds_ConvertsTheUnixEpochItself()
    {
        Assert.Equal(0L, FileTimeConversion.ToUnixSeconds(116_444_736_000_000_000L));
    }
}
