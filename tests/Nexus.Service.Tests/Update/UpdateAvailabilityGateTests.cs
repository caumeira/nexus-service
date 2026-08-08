using Nexus.Service.Update;
using Xunit;

namespace Nexus.Service.Tests.Update;

/// <summary>
/// Tests for UpdateService.CanOfferUpdate, the platform gate that keeps
/// Linux (no install path) from surfacing an update it cannot apply.
/// </summary>
public sealed class UpdateAvailabilityGateTests
{
    [Fact]
    public void NewerRelease_NonLinux_IsOffered()
    {
        Assert.True(UpdateService.CanOfferUpdate(isNewer: true, isLinux: false));
    }

    [Fact]
    public void NewerRelease_Linux_IsSuppressed()
    {
        Assert.False(UpdateService.CanOfferUpdate(isNewer: true, isLinux: true));
    }

    [Fact]
    public void NoNewerRelease_NonLinux_IsNotOffered()
    {
        Assert.False(UpdateService.CanOfferUpdate(isNewer: false, isLinux: false));
    }

    [Fact]
    public void NoNewerRelease_Linux_IsNotOffered()
    {
        Assert.False(UpdateService.CanOfferUpdate(isNewer: false, isLinux: true));
    }
}
