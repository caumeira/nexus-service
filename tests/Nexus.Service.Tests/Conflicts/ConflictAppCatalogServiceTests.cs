using System;
using System.Linq;
using Nexus.Service.Conflicts;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

/// <summary>
/// End task stops these services alongside the app's processes, trusting the
/// catalog's name outright - a wrong name here stops an unrelated service.
/// Values captured from a lab box's live service list.
/// </summary>
public class ConflictAppCatalogServiceTests
{
    [Theory]
    [InlineData("signalrgb", "SignalRgb.Service")]
    [InlineData("nzxt-cam", "CAMService")]
    [InlineData("hyte-nexus-2", "HYTEIO")]
    public void TheAppDeclaresTheServiceThatDrivesIt(string id, string serviceName)
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
