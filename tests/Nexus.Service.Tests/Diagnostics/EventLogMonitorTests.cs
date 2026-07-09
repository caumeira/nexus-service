using System;
using System.Threading.Tasks;
using Nexus.Service.Diagnostics.EventLog;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics;

public class EventLogMonitorTests
{
    [Fact]
    public async Task ResetAndBackfillAsync_LeavesStoreQueryable()
    {
        var monitor = new EventLogMonitor();

        await monitor.ResetAndBackfillAsync();

        Assert.Empty(monitor.Snapshot(30));
        Assert.Empty(monitor.CountsSince(TimeSpan.FromDays(30)));
    }
}
