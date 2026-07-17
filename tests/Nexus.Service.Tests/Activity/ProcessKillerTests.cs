using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class ProcessKillerTests
{
    private static Process SpawnSacrificialProcess()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >NUL")
            : new ProcessStartInfo("/bin/sleep", "30");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        return Process.Start(psi)!;
    }

    [Fact]
    public void KillAllCounted_OnEmptyList_ReturnsZeroZero()
    {
        var (killed, failed) = ProcessKiller.KillAllCounted(Array.Empty<Process>());

        Assert.Equal(0, killed);
        Assert.Equal(0, failed);
    }

    [Fact]
    public void KillAllCounted_KillsRealLiveProcesses_AndReportsThemAsKilled()
    {
        var handles = new List<Process>();
        var pids = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var proc = SpawnSacrificialProcess();
            handles.Add(proc);
            pids.Add(proc.Id);
        }

        var (killed, failed) = ProcessKiller.KillAllCounted(handles);

        Assert.Equal(3, killed);
        Assert.Equal(0, failed);
        foreach (var pid in pids)
        {
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        }
    }

    [Fact]
    public void KillAllCounted_TreatsAProcessThatExitedBeforeOurTurn_AsKilledNotFailed()
    {
        // Simulates a tree-kill cascade taking a sibling down before this
        // loop reaches it: each handle is a live .NET object, but the
        // underlying OS process is already gone by the time KillAllCounted
        // calls Kill() on it - the exact condition Process.Kill() documents
        // as InvalidOperationException, not a real failure.
        var handles = new List<Process>();
        for (var i = 0; i < 4; i++)
        {
            var proc = SpawnSacrificialProcess();
            using (var separateHandle = Process.GetProcessById(proc.Id))
            {
                separateHandle.Kill(true);
                separateHandle.WaitForExit(5000);
            }
            handles.Add(proc);
        }

        var (killed, failed) = ProcessKiller.KillAllCounted(handles);

        Assert.Equal(4, killed);
        Assert.Equal(0, failed);
    }
}
