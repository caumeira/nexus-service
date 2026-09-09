using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

/// <summary>
/// A probe child that dies inside GL init used to read as "inconclusive", so the
/// parent repeated the same fatal init in its own process. The exit code alone
/// cannot say whether the card did the killing, which is what the GLINIT_ENTER
/// marker settles.
/// </summary>
public class GpuProbeExitTests
{
    // The verdict enum is internal, so the expectation travels as its name.
    [Theory]
    [InlineData(0, true, "Works")]
    [InlineData(0, false, "Works")]
    [InlineData(1, true, "Failed")]
    [InlineData(2, true, "Inconclusive")]
    public void DeliberateExitCodesIgnoreTheMarker(int exitCode, bool sawMarker, string expected)
    {
        Assert.Equal(expected, GpuProbeExit.Verdict(exitCode, sawMarker, timedOut: false).ToString());
    }

    [Fact]
    public void WglFastFailAfterTheMarker_CondemnsTheCard()
    {
        // 0xC0000409 as the parent reads it.
        Assert.Equal(GpuProbeVerdict.Failed,
            GpuProbeExit.Verdict(-1073740791, sawEnterMarker: true, timedOut: false));
    }

    [Fact]
    public void CrashBeforeTheMarker_DoesNotCondemnTheCard()
    {
        Assert.Equal(GpuProbeVerdict.Inconclusive,
            GpuProbeExit.Verdict(-1073740791, sawEnterMarker: false, timedOut: false));
    }

    [Fact]
    public void AccessViolationAfterTheMarker_CondemnsTheCard()
    {
        Assert.Equal(GpuProbeVerdict.Failed,
            GpuProbeExit.Verdict(-1073741819, sawEnterMarker: true, timedOut: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1073740791)]
    public void AKilledChildIsNeverAVerdict(int exitCode)
    {
        Assert.Equal(GpuProbeVerdict.Inconclusive,
            GpuProbeExit.Verdict(exitCode, sawEnterMarker: true, timedOut: true));
    }

    [Fact]
    public void UnknownPositiveExit_StaysInconclusiveWithoutTheMarker()
    {
        Assert.Equal(GpuProbeVerdict.Inconclusive, GpuProbeExit.Verdict(9, false, false));
        Assert.Equal(GpuProbeVerdict.Failed, GpuProbeExit.Verdict(9, true, false));
    }
}
