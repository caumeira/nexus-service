#if MACOS
using Nexus.Service.Platform.Mac;

namespace Nexus.Service.Tests;

public class MacDiskStatsTests
{
    // Counters are cumulative since driver load: any running Mac has read a
    // nonzero number of bytes from its boot volume, and consecutive reads of
    // a monotonic counter can only grow. A volume ejecting between reads
    // shrinks the sum (DiskRateReader's negative-delta guard covers that in
    // production); the test re-baselines once so that race cannot red the
    // suite.
    [MacOnlyFact]
    public void TryReadCumulativeBytes_ReturnsMonotonicPositiveTotals()
    {
        Assert.True(MacDiskStats.TryReadCumulativeBytes(out var read1, out var written1));
        Assert.True(read1 > 0, $"expected positive cumulative reads, got {read1}");
        Assert.True(written1 >= 0, $"expected non-negative cumulative writes, got {written1}");

        Assert.True(MacDiskStats.TryReadCumulativeBytes(out var read2, out var written2));
        if (read2 < read1 || written2 < written1)
        {
            read1 = read2;
            written1 = written2;
            Assert.True(MacDiskStats.TryReadCumulativeBytes(out read2, out written2));
        }
        Assert.True(read2 >= read1, $"reads went backwards: {read1} -> {read2}");
        Assert.True(written2 >= written1, $"writes went backwards: {written1} -> {written2}");
    }
}
#endif
