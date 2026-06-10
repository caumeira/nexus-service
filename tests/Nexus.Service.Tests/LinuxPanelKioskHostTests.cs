using System.Collections.Generic;
using Nexus.Service.Platform.Linux;
using Xunit;

namespace Nexus.Service.Tests;

public class LinuxPanelKioskHostTests
{
    private static Dictionary<string, string> Running(params (string DisplayId, string DeviceId)[] entries)
    {
        var map = new Dictionary<string, string>();
        foreach (var (displayId, deviceId) in entries)
            map[displayId] = deviceId;
        return map;
    }

    [Fact]
    public void Diff_OpensDesiredNotRunning()
    {
        var (toClose, toOpen) = LinuxPanelKioskHost.Diff(
            Running(),
            new List<(string, string)> { ("DELA0B8-1", "dev1") });

        Assert.Empty(toClose);
        Assert.Equal(new[] { ("DELA0B8-1", "dev1") }, toOpen);
    }

    [Fact]
    public void Diff_ClosesRunningNotDesired()
    {
        var (toClose, toOpen) = LinuxPanelKioskHost.Diff(
            Running(("DELA0B8-1", "dev1")),
            new List<(string, string)>());

        Assert.Equal(new[] { "DELA0B8-1" }, toClose);
        Assert.Empty(toOpen);
    }

    [Fact]
    public void Diff_DeviceSwapClosesAndReopens()
    {
        var (toClose, toOpen) = LinuxPanelKioskHost.Diff(
            Running(("DELA0B8-1", "dev1")),
            new List<(string, string)> { ("DELA0B8-1", "dev2") });

        Assert.Equal(new[] { "DELA0B8-1" }, toClose);
        Assert.Equal(new[] { ("DELA0B8-1", "dev2") }, toOpen);
    }

    [Fact]
    public void Diff_UnchangedUntouched()
    {
        var (toClose, toOpen) = LinuxPanelKioskHost.Diff(
            Running(("DELA0B8-1", "dev1"), ("GSM5C1D-2", "dev2")),
            new List<(string, string)> { ("DELA0B8-1", "dev1"), ("GSM5C1D-2", "dev2") });

        Assert.Empty(toClose);
        Assert.Empty(toOpen);
    }

    [Fact]
    public void Diff_MixedAddRemoveKeep()
    {
        var (toClose, toOpen) = LinuxPanelKioskHost.Diff(
            Running(("keep", "dev1"), ("gone", "dev2")),
            new List<(string, string)> { ("keep", "dev1"), ("new", "dev3") });

        Assert.Equal(new[] { "gone" }, toClose);
        Assert.Equal(new[] { ("new", "dev3") }, toOpen);
    }
}
