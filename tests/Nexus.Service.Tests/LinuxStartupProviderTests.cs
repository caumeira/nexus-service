using System.Collections.Generic;
using Nexus.Service.Lifecycle;
using Xunit;

namespace Nexus.Service.Tests;

public sealed class LinuxStartupProviderTests
{
    private sealed class FakeSystemctl
    {
        public readonly List<string[]> Runs = new();
        public readonly List<string[]> Queries = new();
        public int RunExit;
        public string QueryResult = "disabled";

        public int Run(string[] args) { Runs.Add(args); return RunExit; }
        public string Query(string[] args) { Queries.Add(args); return QueryResult; }
    }

    [Fact]
    public void SetEnabled_true_enables_the_system_unit()
    {
        var fake = new FakeSystemctl();
        var p = new LinuxStartupProvider(fake.Run, fake.Query);

        Assert.True(p.SetEnabled(true, "/opt/nexus/Nexus", ""));
        Assert.Single(fake.Runs);
        Assert.Equal(new[] { "enable", LinuxStartupProvider.UnitName }, fake.Runs[0]);
    }

    [Fact]
    public void SetEnabled_false_disables_the_system_unit()
    {
        var fake = new FakeSystemctl();
        var p = new LinuxStartupProvider(fake.Run, fake.Query);

        Assert.True(p.SetEnabled(false, "/opt/nexus/Nexus", ""));
        Assert.Equal(new[] { "disable", LinuxStartupProvider.UnitName }, fake.Runs[0]);
    }

    [Fact]
    public void SetEnabled_reports_failure_when_systemctl_fails()
    {
        // dev --user run / not root / unit not installed → exit != 0 → false.
        var fake = new FakeSystemctl { RunExit = 1 };
        var p = new LinuxStartupProvider(fake.Run, fake.Query);

        Assert.False(p.SetEnabled(true, "/opt/nexus/Nexus", ""));
    }

    [Fact]
    public void IsEnabled_true_only_when_systemctl_reports_enabled()
    {
        var fake = new FakeSystemctl { QueryResult = "enabled\n" };
        var p = new LinuxStartupProvider(fake.Run, fake.Query);

        Assert.True(p.IsEnabled());
        Assert.Equal(new[] { "is-enabled", LinuxStartupProvider.UnitName }, fake.Queries[0]);
    }

    [Fact]
    public void IsEnabled_false_when_disabled()
    {
        var fake = new FakeSystemctl { QueryResult = "disabled\n" };
        var p = new LinuxStartupProvider(fake.Run, fake.Query);

        Assert.False(p.IsEnabled());
    }
}
