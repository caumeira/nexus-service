using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Conflicts;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

/// <summary>
/// The catalog-wide install match behind the startup snapshot's conflicts
/// section. Runs off a supplied service list, so it exercises the same matching
/// the Windows probe does without an SCM to enumerate.
/// </summary>
public class ConflictAppInstallProbeTests
{
    private static ConflictAppDefinition App(string id) => ConflictWatcher.FindById(id)!;

    [Fact]
    public void MatchesOnTheServiceKey()
    {
        var services = new List<(string, string)> { ("CorsairDeviceListerService", "Corsair Device Lister") };
        Assert.True(ConflictAppInstallProbe.HasService(App("icue"), services));
    }

    [Fact]
    public void MatchesAProcessNameRegisteredAsAService()
    {
        // A vendor may register what the catalog lists as a process name as the
        // service key instead, so both lists are matched against both fields.
        var services = new List<(string, string)> { ("CorsairService", "Corsair Service") };
        Assert.True(ConflictAppInstallProbe.HasService(App("icue"), services));
    }

    [Fact]
    public void MatchesADisplayNameOnlyAfterNormalizing()
    {
        var services = new List<(string, string)> { ("SomeKey", "Corsair Device Control Service") };
        Assert.True(ConflictAppInstallProbe.HasService(App("icue"), services));
    }

    [Fact]
    public void DoesNotMatchAnUnrelatedService()
    {
        var services = new List<(string, string)> { ("Spooler", "Print Spooler") };
        Assert.False(ConflictAppInstallProbe.HasService(App("icue"), services));
    }

    [Fact]
    public void AnAppWithNoNamesNeverMatches()
    {
        var def = new ConflictAppDefinition { Id = "nameless", DisplayName = "Nameless" };
        var services = new List<(string, string)> { ("Spooler", "Print Spooler") };
        Assert.False(ConflictAppInstallProbe.HasService(def, services));
    }

    [Fact]
    public void IsInstalledAnswersPerAppAndNeverThrows()
    {
        var probe = new ConflictAppInstallProbe();
        Assert.False(probe.IsInstalled("no-such-app"));
        // Repeat reads come from the per-app cache.
        Assert.Equal(probe.IsInstalled("icue"), probe.IsInstalled("icue"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(probe.IsInstalled("icue"));
        }
    }

    [Fact]
    public void MatchesIgnoresPunctuationAndRejectsEmptyNames()
    {
        var wanted = new[] { "CorsairDeviceControlService" };
        Assert.True(ConflictAppInstallProbe.Matches(wanted, "Corsair Device Control Service"));
        Assert.False(ConflictAppInstallProbe.Matches(wanted, "CorsairDeviceControl"));
        Assert.False(ConflictAppInstallProbe.Matches(wanted, "   "));
        Assert.False(ConflictAppInstallProbe.Matches(wanted, ""));
    }

    [Fact]
    public void InstalledAppIdsOnlyEverReportsCatalogIds()
    {
        // The list is built by iterating the catalog, so the subset assertion is a
        // guard against a future rewrite rather than a live risk. What this earns
        // today is running the SCM enumeration and its P/Invoke without throwing.
        var ids = new ConflictAppInstallProbe().InstalledAppIds();
        var known = ConflictAppCatalog.All.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(ids, id => Assert.Contains(id, known));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Empty(ids);
        }
    }
}
