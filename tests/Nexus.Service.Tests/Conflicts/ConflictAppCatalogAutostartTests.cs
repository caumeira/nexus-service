using System;
using System.Linq;
using Nexus.Service.Conflicts;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

/// <summary>
/// Service names are the only autostart mechanism the catalog supplies directly
/// - discovery matches Run values by executable path, but a service is trusted
/// on name alone, so a wrong name here would set an unrelated service to manual.
/// Values captured from a lab box's live service list.
/// </summary>
public class ConflictAppCatalogAutostartTests
{
    [Theory]
    [InlineData("signalrgb", "SignalRgb.Service")]
    [InlineData("nzxt-cam", "CAMService")]
    [InlineData("hyte-nexus-2", "HYTEIO")]
    public void TheAppDeclaresTheServiceThatStartsIt(string id, string serviceName)
    {
        var def = ConflictWatcher.FindById(id);
        Assert.NotNull(def);
        Assert.Contains(serviceName, def!.WindowsServiceNames);
    }

    [Fact]
    public void NoAppClaimsWindowsOwnCameraService()
    {
        // "NZXT CAM" substring-matches Windows' own camsvc (svchost -k
        // osprivacy); setting that to manual would break the OS camera stack.
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
