using Nexus.Service.Update;

namespace Nexus.Service.Tests.Update;

/// <summary>
/// Tests for UpdateService.CanOfferUpdate, the platform gate that keeps
/// the dashboard from offering an update the OTA cannot install (the install
/// handoff exists only on Windows, so macOS and Linux never offer one).
/// </summary>
public class UpdateAvailabilityGateTests
{
    [Fact]
    public void OffersUpdate_WhenNewerAndPlatformCanInstall()
    {
        Assert.True(UpdateService.CanOfferUpdate(isNewer: true, canInstall: true));
    }

    [Fact]
    public void SuppressesUpdate_WhenNewerButPlatformCannotInstall()
    {
        Assert.False(UpdateService.CanOfferUpdate(isNewer: true, canInstall: false));
    }

    [Fact]
    public void NoUpdate_WhenNotNewerEvenIfInstallable()
    {
        Assert.False(UpdateService.CanOfferUpdate(isNewer: false, canInstall: true));
    }

    [Fact]
    public void NoUpdate_WhenNotNewerAndNotInstallable()
    {
        Assert.False(UpdateService.CanOfferUpdate(isNewer: false, canInstall: false));
    }
}
