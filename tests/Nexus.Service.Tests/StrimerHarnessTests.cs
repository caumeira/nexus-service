using Nexus.Service.Peripherals.Strimer;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// The Strimer controller exposes twelve zones, but only some are wired to hardware: six
/// for the 24-pin ATX strip, then either four (dual 8-pin) or six (triple 8-pin) for the
/// GPU harness. The apply latch carries a bitmask of the zones to light, and it used to be
/// a fixed 0x0FFF - which claims a triple harness whatever is actually attached.
/// </summary>
public class StrimerHarnessTests
{
    private static int MaskOf(byte[] latch) => (latch[2] << 8) | latch[3];

    [Fact]
    public void Triple_harness_latches_all_twelve_zones()
    {
        var latch = StrimerProtocol.BuildApplyLatch(StrimerProtocol.GpuZoneCount);

        Assert.Equal(0x0FFF, MaskOf(latch));
        Assert.Equal(StrimerProtocol.ReportId, latch[0]);
        Assert.Equal(0x2C, latch[1]);
    }

    [Fact]
    public void Dual_harness_drops_the_two_zones_it_does_not_have()
    {
        var latch = StrimerProtocol.BuildApplyLatch(StrimerProtocol.GpuZoneCountDual);

        // Bits 0-5 ATX, bits 6-9 GPU. Zones 10 and 11 are absent on this harness.
        Assert.Equal(0x03FF, MaskOf(latch));
    }

    [Fact]
    public void The_default_stays_the_triple_harness()
    {
        Assert.Equal(
            StrimerProtocol.BuildApplyLatch(StrimerProtocol.GpuZoneCount),
            StrimerProtocol.BuildApplyLatch());
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(6, 6)]
    // Anything else is not a harness that exists, so it falls back to the larger one rather
    // than driving a count no hardware has.
    [InlineData(0, 6)]
    [InlineData(5, 6)]
    [InlineData(12, 6)]
    [InlineData(-1, 6)]
    public void Only_the_two_real_harnesses_are_accepted(int configured, int expected)
    {
        Assert.Equal(expected, StrimerProtocol.NormalizeGpuZoneCount(configured));
    }

    [Fact]
    public void Zone_indices_place_the_gpu_harness_after_the_atx_strip()
    {
        Assert.Equal(0, StrimerProtocol.AtxZone(0));
        Assert.Equal(5, StrimerProtocol.AtxZone(5));
        Assert.Equal(6, StrimerProtocol.GpuZone(0));
        // The dual harness ends here; the triple continues to 11.
        Assert.Equal(9, StrimerProtocol.GpuZone(3));
        Assert.Equal(11, StrimerProtocol.GpuZone(5));
    }

    /// <summary>Both harnesses use the same 27-LED zones; only the count differs.</summary>
    [Fact]
    public void Led_counts_are_unchanged_by_the_harness()
    {
        Assert.Equal(20, StrimerProtocol.AtxLedsPerZone);
        Assert.Equal(27, StrimerProtocol.GpuLedsPerZone);
        Assert.Equal(120, StrimerProtocol.AtxZoneCount * StrimerProtocol.AtxLedsPerZone);
        Assert.Equal(108, StrimerProtocol.GpuZoneCountDual * StrimerProtocol.GpuLedsPerZone);
        Assert.Equal(162, StrimerProtocol.GpuZoneCount * StrimerProtocol.GpuLedsPerZone);
    }
}
