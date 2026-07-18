using System.Linq;
using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class ProcessAggregationTests
{
    private static ProcessInfo Proc(
        string name, double cpu, double mem, long? startedAtMs = null, bool hasWindow = false, double storage = 0,
        double storageRead = 0, double storageWrite = 0) =>
        new()
        {
            Name = name, CpuPercent = cpu, MemoryMb = mem, StartedAtMs = startedAtMs, HasWindow = hasWindow,
            StorageBytesPerSec = storage, StorageReadBytesPerSec = storageRead, StorageWriteBytesPerSec = storageWrite,
        };

    [Fact]
    public void GroupByName_SumsCpuAndMemory_AcrossPidsWithTheSameName()
    {
        var procs = new[]
        {
            Proc("chrome", cpu: 5, mem: 100),
            Proc("chrome", cpu: 7, mem: 150),
        };

        var grouped = ProcessAggregation.GroupByName(procs);

        var chrome = grouped["chrome"];
        Assert.Equal(12, chrome.CpuPercent);
        Assert.Equal(250, chrome.MemoryMb);
    }

    [Fact]
    public void GroupByName_KeepsEachDistinctNameSeparate()
    {
        var procs = new[] { Proc("chrome", cpu: 5, mem: 100), Proc("notepad", cpu: 1, mem: 20) };

        var grouped = ProcessAggregation.GroupByName(procs);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void GroupByName_UsesTheNewestInstancesStartedAtMs_NotTheOldest()
    {
        var procs = new[]
        {
            Proc("chrome", cpu: 5, mem: 100, startedAtMs: 5000),
            Proc("chrome", cpu: 7, mem: 150, startedAtMs: 9000),
        };

        var grouped = ProcessAggregation.GroupByName(procs);

        Assert.Equal(9000, grouped["chrome"].StartedAtMs);
    }

    [Fact]
    public void GroupByName_UsesTheNewestInstancesStartedAtMs_RegardlessOfInputOrder()
    {
        var procs = new[]
        {
            Proc("chrome", cpu: 5, mem: 100, startedAtMs: 9000),
            Proc("chrome", cpu: 7, mem: 150, startedAtMs: 5000),
        };

        var grouped = ProcessAggregation.GroupByName(procs);

        Assert.Equal(9000, grouped["chrome"].StartedAtMs);
    }

    [Fact]
    public void GroupByName_KeepsAKnownStartedAtMs_WhenAnotherInstanceHasNone()
    {
        var procs = new[]
        {
            Proc("chrome", cpu: 5, mem: 100, startedAtMs: null),
            Proc("chrome", cpu: 7, mem: 150, startedAtMs: 5000),
        };

        var grouped = ProcessAggregation.GroupByName(procs);

        Assert.Equal(5000, grouped["chrome"].StartedAtMs);
    }

    [Fact]
    public void GroupByName_StartedAtMsStaysNull_WhenNoInstanceReportsOne()
    {
        var procs = new[] { Proc("chrome", cpu: 5, mem: 100) };

        var grouped = ProcessAggregation.GroupByName(procs);

        Assert.Null(grouped["chrome"].StartedAtMs);
    }

    [Fact]
    public void GroupByName_ReturnsEmpty_ForAnEmptyInput()
    {
        var grouped = ProcessAggregation.GroupByName(System.Array.Empty<ProcessInfo>());

        Assert.Empty(grouped);
    }

    [Fact]
    public void GroupByName_SumsStorageBytesPerSec_AcrossPidsWithTheSameName()
    {
        var procs = new[]
        {
            Proc("chrome", cpu: 5, mem: 100, storage: 1000),
            Proc("chrome", cpu: 7, mem: 150, storage: 2500),
        };

        var grouped = ProcessAggregation.GroupByName(procs);

        Assert.Equal(3500, grouped["chrome"].StorageBytesPerSec);
    }

    [Fact]
    public void GroupByName_SumsStorageReadAndWriteBytesPerSec_AcrossPidsWithTheSameName()
    {
        var procs = new[]
        {
            Proc("chrome", cpu: 5, mem: 100, storageRead: 1000, storageWrite: 200),
            Proc("chrome", cpu: 7, mem: 150, storageRead: 2500, storageWrite: 400),
        };

        var grouped = ProcessAggregation.GroupByName(procs);

        Assert.Equal(3500, grouped["chrome"].StorageReadBytesPerSec);
        Assert.Equal(600, grouped["chrome"].StorageWriteBytesPerSec);
    }

    [Fact]
    public void GroupByName_HasWindowIsTrue_WhenAnyInstanceOwnsAWindow()
    {
        var procs = new[]
        {
            Proc("chrome", cpu: 5, mem: 100, hasWindow: false),
            Proc("chrome", cpu: 7, mem: 150, hasWindow: true),
        };

        var grouped = ProcessAggregation.GroupByName(procs);

        Assert.True(grouped["chrome"].HasWindow);
    }

    [Fact]
    public void GroupByName_HasWindowIsFalse_WhenNoInstanceOwnsAWindow()
    {
        var procs = new[] { Proc("svchost", cpu: 1, mem: 10, hasWindow: false) };

        var grouped = ProcessAggregation.GroupByName(procs);

        Assert.False(grouped["svchost"].HasWindow);
    }

    [Fact]
    public void GroupByName_HasWindowIsTrue_RegardlessOfWhichInstanceComesFirst()
    {
        var procs = new[]
        {
            Proc("chrome", cpu: 5, mem: 100, hasWindow: true),
            Proc("chrome", cpu: 7, mem: 150, hasWindow: false),
        };

        var grouped = ProcessAggregation.GroupByName(procs);

        Assert.True(grouped["chrome"].HasWindow);
    }
}
