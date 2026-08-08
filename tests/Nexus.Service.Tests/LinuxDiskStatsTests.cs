#if LINUX
using Nexus.Service.Platform.Linux;

namespace Nexus.Service.Tests;

public class LinuxDiskStatsTests
{
    private const string SampleDiskstats =
        " 259       0 nvme0n1 1000 10 2048 500 2000 20 4096 800 0 900 1300 0 0 0 0 0 0\n" +
        " 259       1 nvme0n1p1 900 9 1024 400 1900 19 2048 700 0 800 1100 0 0 0 0 0 0\n" +
        "   8       0 sda 500 5 1000 200 600 6 3000 300 0 400 500 0 0 0 0 0 0\n" +
        "   7       0 loop0 100 0 512 50 0 0 0 0 0 50 50 0 0 0 0 0 0\n" +
        " 253       0 dm-0 400 0 4000 100 500 0 5000 200 0 250 300 0 0 0 0 0 0\n";

    // Whole disks only: the partition (nvme0n1p1) is not in the disk set, and
    // loop/dm names are filtered before the set is built - their sectors must
    // not double-count.
    [Fact]
    public void TryParse_SumsOnlyListedWholeDisks()
    {
        var disks = new HashSet<string> { "nvme0n1", "sda" };

        Assert.True(LinuxDiskStats.TryParse(SampleDiskstats, disks, out var read, out var written));
        Assert.Equal((2048 + 1000) * 512L, read);
        Assert.Equal((4096 + 3000) * 512L, written);
    }

    [Fact]
    public void TryParse_ReturnsFalse_WhenNoListedDiskPresent()
    {
        var disks = new HashSet<string> { "sdz" };

        Assert.False(LinuxDiskStats.TryParse(SampleDiskstats, disks, out var read, out var written));
        Assert.Equal(0, read);
        Assert.Equal(0, written);
    }

    // Counters are cumulative since boot: any running Linux box has read a
    // nonzero number of bytes from its root disk, and consecutive reads of a
    // monotonic counter can only grow. A disk detaching between reads shrinks
    // the sum (DiskRateReader's negative-delta guard covers that in
    // production); the test re-baselines once so that race cannot red the
    // suite.
    [LinuxOnlyFact]
    public void TryReadCumulativeBytes_ReturnsMonotonicPositiveTotals()
    {
        Assert.True(LinuxDiskStats.TryReadCumulativeBytes(out var read1, out var written1));
        Assert.True(read1 > 0, $"expected positive cumulative reads, got {read1}");
        Assert.True(written1 >= 0, $"expected non-negative cumulative writes, got {written1}");

        Assert.True(LinuxDiskStats.TryReadCumulativeBytes(out var read2, out var written2));
        if (read2 < read1 || written2 < written1)
        {
            read1 = read2;
            written1 = written2;
            Assert.True(LinuxDiskStats.TryReadCumulativeBytes(out read2, out written2));
        }
        Assert.True(read2 >= read1, $"reads went backwards: {read1} -> {read2}");
        Assert.True(written2 >= written1, $"writes went backwards: {written1} -> {written2}");
    }
}
#endif
