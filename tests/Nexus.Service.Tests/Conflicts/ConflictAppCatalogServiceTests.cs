using System;
using System.Linq;
using Nexus.Service.Conflicts;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

/// <summary>
/// Locks the service names End task stops against silent deletion, and bans
/// the one name that would stop an unrelated service. Whether a name matches
/// the vendor's real service cannot be checked here - these were captured from
/// a lab box's live service list.
/// </summary>
public class ConflictAppCatalogServiceTests
{
    [Theory]
    [InlineData("signalrgb", "SignalRgb.Service")]
    [InlineData("nzxt-cam", "CAMService")]
    [InlineData("hyte-nexus-2", "HYTEIO")]
    public void TheServiceNameStaysInTheCatalog(string id, string serviceName)
    {
        var def = ConflictWatcher.FindById(id);
        Assert.NotNull(def);
        Assert.Contains(serviceName, def!.WindowsServiceNames);
    }

    [Fact]
    public void NoAppClaimsWindowsOwnCameraService()
    {
        // "NZXT CAM" substring-matches Windows' own camsvc (svchost -k
        // osprivacy); stopping that would break the OS camera stack.
        foreach (var def in ConflictAppCatalog.All)
        {
            Assert.DoesNotContain("camsvc", def.WindowsServiceNames, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ServiceNamesAreNonEmptyAndUnique()
    {
        foreach (var def in ConflictAppCatalog.All)
        {
            Assert.All(def.WindowsServiceNames, n => Assert.False(string.IsNullOrWhiteSpace(n)));
            Assert.Equal(def.WindowsServiceNames.Length, def.WindowsServiceNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }
}
